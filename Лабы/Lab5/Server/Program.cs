using FlowerShop.Server;
using FlowerShop.Server.Data;
using FlowerShop.Server.Replication;

// ===== ЛР №5. Точка входа сервера: репликация и отказоустойчивость. =====
// Продолжение ЛР №4. Данные предметной области хранятся в ДВУХ независимых
// экземплярах MSSQL:
//   Primary — база FlowerShop         (основной экземпляр);
//   Replica — база FlowerShopReplica  (резервная копия).
// Начальное состояние Primary = Replica. Изменения выполняются в Primary и
// передаются в Replica (репликация). При отказе Primary выполняется Failover
// на Replica; после восстановления — Recovery и синхронизация.
//
// Механизм синхронизации общего ресурса (из ЛР №3) выбирается аргументом --sync:
//   dotnet run --project Server -- --sync=lock        (основной вариант)
//   dotnet run --project Server -- --sync=semaphore   (альтернативный вариант)
//   dotnet run --project Server -- --sync=none        (поведение ЛР №2 — Race Condition)
// Первый числовой аргумент — порт (по умолчанию 5000).
// Флаг --skip-db-init пропускает создание схемы (если базы уже инициализированы).

int port = Server.DefaultPort;
SyncMechanism sync = SyncMechanism.Lock;
bool skipDbInit = false;

foreach (string arg in args)
{
    if (arg.StartsWith("--sync=", StringComparison.OrdinalIgnoreCase))
    {
        string raw = arg["--sync=".Length..];
        if (!Enum.TryParse(raw, ignoreCase: true, out sync))
        {
            Console.WriteLine($"Неизвестный механизм синхронизации: '{raw}'. Доступны: none, lock, semaphore");
            return;
        }
    }
    else if (arg.Equals("--skip-db-init", StringComparison.OrdinalIgnoreCase))
    {
        skipDbInit = true;
    }
    else if (int.TryParse(arg, out int p))
    {
        port = p;
    }
}

// ===== ЛР №5, часть 1: создание Primary и Replica ==========================
Console.WriteLine("[Сервер] ЛР №5: инициализация Primary и Replica...");

var primaryDb = new Database(Database.DefaultDatabase);      // FlowerShop
var replicaDb = new Database(Database.ReplicaDatabase);      // FlowerShopReplica

// Проверка доступности сервера СУБД и создание баз (Требование 8).
foreach (var db in new[] { primaryDb, replicaDb })
{
    try
    {
        db.EnsureDatabaseExists();
    }
    catch (OperationException ex)
    {
        Console.WriteLine($"[Сервер] ОШИБКА: сервер СУБД недоступен для '{db.DatabaseName}' — {ex.Message}");
        Console.WriteLine("[Сервер] Убедитесь, что запущен контейнер mssql/server:2022-latest:");
        Console.WriteLine("[Сервер] docker run -e 'ACCEPT_EULA=Y' -e 'MSSQL_SA_PASSWORD=MyStrongPassword123!' -p 1433:1433 --name flowershop-db -d mcr.microsoft.com/mssql/server:2022-latest");
        return;
    }

    if (!db.TryPing(out string dbError))
    {
        Console.WriteLine($"[Сервер] ОШИБКА: БД '{db.DatabaseName}' недоступна — {dbError}");
        return;
    }
    Console.WriteLine($"[Сервер] БД '{db.DatabaseName}' доступна ✓");
}

// Инициализация схемы обеих копий (Требование 4 ч.1: одинаковое начальное состояние).
if (!skipDbInit)
{
    try
    {
        string schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
        if (File.Exists(schemaPath))
        {
            Console.WriteLine("[Сервер] Создание схемы Primary и Replica...");
            primaryDb.EnsureSchema(schemaPath);
            replicaDb.EnsureSchema(schemaPath);
            Console.WriteLine("[Сервер] Схема Primary и Replica инициализирована ✓");
        }
        else
        {
            Console.WriteLine($"[Сервер] ВНИМАНИЕ: schema.sql не найден по пути {schemaPath}");
        }
    }
    catch (OperationException ex)
    {
        Console.WriteLine($"[Сервер] ОШИБКА при инициализации схемы: {ex.Message}");
        return;
    }
}

// Менеджер репликации: Primary + Replica.
var primary = new StorageInstance(ReplicaRole.Primary, primaryDb);
var replica = new StorageInstance(ReplicaRole.Replica, replicaDb);
var replication = new ReplicationManager(primary, replica);
replication.Logger = (phase, detail) => Console.WriteLine($"[Репликация] {phase}: {detail}");

// Требование 7 ч.1: убедимся, что начальное состояние Primary = Replica.
replication.RefreshStates();
ConsistencyReport initial = replication.CheckConsistency();
Console.WriteLine(initial.Consistent
    ? "[Сервер] Начальное состояние: Primary = Replica ✓"
    : $"[Сервер] ВНИМАНИЕ: начальное состояние различается: {string.Join("; ", initial.Differences)}");

var logic = new BusinessLogic(sync: sync, replication: replication);
var server = new Server(port, logic: logic);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Сервер] Остановка...");
    server.Stop();
    Environment.Exit(0);
};

Console.WriteLine($"[Сервер] ЛР №5: механизм синхронизации общего ресурса — {sync}");
Console.WriteLine($"[Сервер] Primary={replication.PrimaryState} ({Database.DefaultDatabase}), " +
                  $"Replica={replication.ReplicaState} ({Database.ReplicaDatabase}), " +
                  $"активный={replication.ActiveName}");
Console.WriteLine($"[Сервер] Сервер запущен на порту {port}");

// Сервер работает асинхронно: каждый клиент обслуживается в отдельной Task.
await server.StartAsync();
