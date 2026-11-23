using System.Text;
using FlowerShop.Server;
using FlowerShop.Server.Consensus;
using FlowerShop.Server.Data;
using FlowerShop.Server.PubSub;
using FlowerShop.Server.Services;
using FlowerShop.Shared.Protocol;

// ЛР №7/№8: сервер кластера из 5 узлов (выбор лидера, heartbeat, голосование, консенсус)
// с постоянным хранением заказов в MSSQL (ЛР № 8).
// Параметры: --port N | --log путь | --data папка | --monitor-ms N | --missed N | --experiments

Console.OutputEncoding = Encoding.UTF8;

static string? Arg(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.Contains("--experiments"))
{
    // Автономный запуск экспериментов части 6 с печатью журнала.
    var results = ConsensusExperiments.RunAll(Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine("===== Итоги экспериментов =====");
    foreach (var r in results)
        Console.WriteLine($"Эксперимент {r.Number}: доступно {r.AvailableNodes}/{r.TotalNodes}, ожидалось {r.Expected}, получено {r.Actual} — {(r.Passed ? "OK" : "ОШИБКА")}");
    return results.All(r => r.Passed) ? 0 : 1;
}

// Команда имеет приоритет, затем переменные окружения, затем значения по умолчанию (ЛР № 8).
int port = int.TryParse(Arg(args, "--port"), out int p)
    ? p
    : (int.TryParse(Environment.GetEnvironmentVariable("FlowerShop__Port"), out int ep) ? ep : ProtocolDefaults.DefaultPort);
string logPath = Arg(args, "--log")
    ?? Environment.GetEnvironmentVariable("FlowerShop__LogPath")
    ?? Path.Combine("data", "consensus.log");
string dataPath = Arg(args, "--data")
    ?? Environment.GetEnvironmentVariable("FlowerShop__DataPath")
    ?? Path.Combine("data", "events");
int monitorMs = int.TryParse(Arg(args, "--monitor-ms"), out int m)
    ? Math.Max(200, m)
    : (int.TryParse(Environment.GetEnvironmentVariable("FlowerShop__MonitorMs"), out int em) ? Math.Max(200, em) : 3000);
int missed = int.TryParse(Arg(args, "--missed"), out int k)
    ? Math.Max(1, k)
    : (int.TryParse(Environment.GetEnvironmentVariable("FlowerShop__Missed"), out int ek) ? Math.Max(1, ek) : 2);

// ---- инициализация MSSQL (ЛР № 8) ------------------------------------------
var db = new Database();
string? dbError;

// Проверка доступности сервера БД.
if (!db.TryPing(out dbError))
{
    Console.WriteLine($"[Сервер] MSSQL недоступен: {dbError}");
    Console.WriteLine("[Сервер] Продолжаем работу без базы данных (заказы только в памяти).");
    db = null!;
}
else
{
    Console.WriteLine("[Сервер] MSSQL доступен ✓");

    // Создать базу, если не существует.
    if (!db.EnsureDatabaseExists(out dbError))
        Console.WriteLine($"[Сервер] Не удалось создать базу: {dbError}");
    else
        Console.WriteLine($"[Сервер] База '{db.DatabaseName}' готова ✓");

    // Применить схему.
    if (!db.EnsureSchema(out dbError))
        Console.WriteLine($"[Сервер] Не удалось применить схему: {dbError}");
    else
        Console.WriteLine("[Сервер] Схема применена ✓");
}

var log = new ConsensusLog(logPath);
log.LineWritten += Console.WriteLine;

var cluster = new ConsensusCluster(log, new ClusterOptions { NodeCount = 5, MissedHeartbeatsToFail = missed });
using var eventStore = new PersistentEventStore(dataPath);
var orderRepository = db is not null ? new OrderRepository(db) : null!;
var orders = new FlowerOrderService(cluster, eventStore, orderRepository);
var router = new RequestRouter(cluster, orders);
var server = new ConsensusTcpServer(router, log);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

cluster.Start();

server.Start(port);
log.Write($"Server listening on port {server.Port} (heartbeat every {monitorMs} ms, timeout after {missed} missed)");

// Фоновая проверка доступности узлов: обнаруживает отказ и запускает повторные выборы.
var monitor = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { await Task.Delay(monitorMs, cts.Token); }
        catch (OperationCanceledException) { break; }

        try { cluster.HeartbeatRound(); }
        catch (Exception ex) { log.Write($"Heartbeat monitor error: {ex.Message}"); }
    }
});

await server.RunAsync(cts.Token);
await monitor;
log.Write("Server stopped");
return 0;
