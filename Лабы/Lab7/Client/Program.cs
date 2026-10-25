using System.Globalization;
using System.Text;
using System.Text.Json;
using FlowerShop.Client;
using FlowerShop.Shared.Consensus;
using FlowerShop.Shared.Protocol;

// ЛР №7: консольный клиент. Параметры: <host> <port> (по умолчанию 127.0.0.1:5000)

Console.OutputEncoding = Encoding.UTF8;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : ProtocolDefaults.DefaultPort;

var jsonOptions = new JsonSerializerOptions(Json.Options) { PropertyNameCaseInsensitive = true };
using var connection = new ServerConnection(host, port);

Console.WriteLine($"Клиент ЛР №7 (вариант 14 — Заказ и доставка цветов). Сервер: {host}:{port}");

while (true)
{
    PrintMenu();
    Console.Write("> ");
    string? choice = Console.ReadLine();
    if (choice is null || choice.Trim() == "0") break;

    try
    {
        switch (choice.Trim())
        {
            case "1": await ShowStatus(); break;
            case "2": await ShowJournal(); break;
            case "3": await CreateOrder(); break;
            case "4": await ChangeStatus(); break;
            case "5": await ListOrders(); break;
            case "6": await ProposeVersion(); break;
            case "7": await NodeCommand("FailNode", "Отказ узла"); break;
            case "8": await NodeCommand("RecoverNode", "Восстановление узла"); break;
            case "9": await Heartbeat(); break;
            case "10": await Elect(); break;
            case "11": await Dissent(); break;
            case "12": await Experiments(); break;
            default: Console.WriteLine("Неизвестный пункт меню."); break;
        }
    }
    catch (Exception ex)
    {
        // Любая ошибка ввода/разбора не должна завершать клиент.
        Console.WriteLine($"Ошибка: {ex.Message}");
    }
}

return 0;

// ---------------------------------------------------------------------------------------------

static void PrintMenu()
{
    Console.WriteLine();
    Console.WriteLine("=== Меню ===");
    Console.WriteLine(" 1  Состояние кластера");
    Console.WriteLine(" 2  Журнал работы узлов");
    Console.WriteLine(" 3  Создать заказ (предложение статуса Created → голосование)");
    Console.WriteLine(" 4  Изменить статус заказа (предложение → голосование)");
    Console.WriteLine(" 5  Список заказов");
    Console.WriteLine(" 6  Предложить ConfigurationVersion");
    Console.WriteLine(" 7  Имитировать отказ узла");
    Console.WriteLine(" 8  Восстановить узел");
    Console.WriteLine(" 9  Проверка heartbeat (обнаружение отказов)");
    Console.WriteLine("10  Принудительные выборы лидера");
    Console.WriteLine("11  Режим «узел голосует против»");
    Console.WriteLine("12  Эксперименты 1–5 (часть 6)");
    Console.WriteLine(" 0  Выход");
}

async Task<Response> Call(string op, object? payload = null)
{
    var r = await connection.SendAsync(op, payload);
    if (r.Success) Console.WriteLine($"[OK] {r.Message}");
    else Console.WriteLine($"[ОШИБКА {r.ErrorCode}] {r.Message}");
    return r;
}

T? Data<T>(Response r) => r.Data.HasValue ? r.Data.Value.Deserialize<T>(jsonOptions) : default;

string Ask(string prompt, string? def = null)
{
    Console.Write(def is null ? $"{prompt}: " : $"{prompt} [{def}]: ");
    string? s = Console.ReadLine();
    return string.IsNullOrWhiteSpace(s) ? (def ?? string.Empty) : s.Trim();
}

int AskInt(string prompt, int? def = null)
{
    string s = Ask(prompt, def?.ToString(CultureInfo.InvariantCulture));
    return int.TryParse(s, out int v) ? v : throw new FormatException($"'{s}' — не число");
}

void PrintStatus(ClusterStatusDto s)
{
    Console.WriteLine($"Эпоха (term): {s.Term}; лидер: {(s.LeaderId is int l ? $"Node {l}" : "нет")}; " +
                      $"доступно {s.AvailableNodes}/{s.TotalNodes}; большинство = {s.Majority}; " +
                      $"{(s.QuorumAvailable ? "консенсус возможен" : "КОНСЕНСУС НЕВОЗМОЖЕН")}");
    Console.WriteLine($"ConfigurationVersion = {s.ConfigurationVersion}; последний индекс = {s.LastCommittedIndex}");
    foreach (var n in s.Nodes)
        Console.WriteLine($"  Node {n.Id}: {n.Status,-9} {n.Role,-8} term={n.Term} index={n.LastCommittedIndex}{(n.Dissent ? "  (голосует против)" : "")}");
}

void PrintProposal(ProposalResultDto? r)
{
    if (r is null) return;
    Console.WriteLine($"Предложение: {r.Description} (индекс {r.Index}, эпоха {r.Term}, лидер Node {r.LeaderId})");
    foreach (var v in r.Votes)
        Console.WriteLine($"  Node {v.NodeId} → {v.Vote}{(string.IsNullOrEmpty(v.Reason) ? "" : $" ({v.Reason})")}");
    Console.WriteLine($"YES = {r.Yes}, NO = {r.No}, NO RESPONSE = {r.NoResponse}; большинство = {r.Majority} из {r.TotalNodes}");
    Console.WriteLine(r.ConsensusAchieved ? "CONSENSUS ACHIEVED" : "CONSENSUS NOT ACHIEVED");
}

async Task ShowStatus()
{
    var r = await Call("GetClusterStatus");
    if (r.Success && Data<ClusterStatusDto>(r) is { } s) PrintStatus(s);
}

async Task ShowJournal()
{
    int last = AskInt("Сколько последних записей", 30);
    var r = await Call("GetJournal", new { last });
    if (r.Success && Data<List<string>>(r) is { } lines)
        foreach (string line in lines) Console.WriteLine(line);
}

async Task CreateOrder()
{
    int customer = AskInt("Id клиента", 1);
    int bouquet = AskInt("Id букета", 1);
    string address = Ask("Адрес доставки", "ул. Цветочная, 5");
    var date = DateTime.Today.AddDays(1);
    var r = await Call("CreateOrder", new { customerId = customer, bouquetId = bouquet, deliveryDate = date, deliveryAddress = address });
    PrintOrderResponse(r);
}

async Task ChangeStatus()
{
    int id = AskInt("Id заказа");
    Console.WriteLine("Статусы: Created, BouquetPrepared, FloristAssigned, DeliveryArranged, Cancelled");
    string status = Ask("Новый статус", "BouquetPrepared");
    int? florist = null;
    if (status == "FloristAssigned") florist = AskInt("Id флориста", 1);
    var r = await Call("ChangeOrderStatus", new { orderId = id, status, floristId = florist });
    PrintOrderResponse(r);
}

void PrintOrderResponse(Response r)
{
    if (!r.Data.HasValue) return;
    var el = r.Data.Value;
    if (el.TryGetProperty("Order", out var o) && o.ValueKind == JsonValueKind.Object)
    {
        var order = o.Deserialize<FlowerShop.Shared.Models.Order>(jsonOptions);
        if (order is not null)
            Console.WriteLine($"Заказ #{order.Id}: статус {order.Status}; события [{string.Join(" → ", order.Events)}]; " +
                              $"адрес «{order.DeliveryAddress}»{(order.FloristId is int f ? $"; флорист {f}" : "")}");
    }
    if (el.TryGetProperty("Consensus", out var c) && c.ValueKind == JsonValueKind.Object)
        PrintProposal(c.Deserialize<ProposalResultDto>(jsonOptions));
}

async Task ListOrders()
{
    var r = await Call("ListOrders");
    if (!r.Success || !r.Data.HasValue) return;
    foreach (var o in r.Data.Value.EnumerateArray())
        Console.WriteLine($"  #{o.GetProperty("Id").GetInt32()}: {o.GetProperty("Status").GetString()}, " +
                          $"клиент {o.GetProperty("CustomerId").GetInt32()}, букет {o.GetProperty("BouquetId").GetInt32()}, " +
                          $"адрес «{o.GetProperty("DeliveryAddress").GetString()}»");
}

async Task ProposeVersion()
{
    int version = AskInt("ConfigurationVersion");
    var r = await Call("ProposeConfigurationVersion", new { version });
    PrintProposal(Data<ProposalResultDto>(r));
}

async Task NodeCommand(string op, string title)
{
    Console.WriteLine(title);
    int id = AskInt("Номер узла (1–5)");
    var r = await Call(op, new { nodeId = id });
    if (r.Success && Data<ClusterStatusDto>(r) is { } s) PrintStatus(s);
}

async Task Heartbeat()
{
    var r = await Call("HeartbeatRound");
    if (r.Success && Data<HeartbeatReportDto>(r) is { } h)
        Console.WriteLine($"Отказавшие: [{string.Join(", ", h.NewlyFailed)}]; лидер отказал: {h.LeaderFailed}; " +
                          $"выборы: {h.ElectionTriggered}; лидер: {(h.LeaderId is int l ? $"Node {l}" : "нет")}");
}

async Task Elect()
{
    var r = await Call("ElectLeader");
    if (r.Success && Data<ElectionResultDto>(r) is { } e)
        Console.WriteLine($"Участвуют: [{string.Join(", ", e.Participants)}]; исключены: [{string.Join(", ", e.Excluded)}]");
}

async Task Dissent()
{
    int id = AskInt("Номер узла (1–5)");
    bool on = Ask("Голосовать против? (y/n)", "y").StartsWith('y');
    var r = await Call("SetDissent", new { nodeId = id, dissent = on });
    if (r.Success && Data<ClusterStatusDto>(r) is { } s) PrintStatus(s);
}

async Task Experiments()
{
    int n = AskInt("Номер эксперимента (1–5, 0 — все)", 0);
    if (n == 0)
    {
        var all = await Call("RunAllExperiments");
        if (all.Success && Data<List<ExperimentResultDto>>(all) is { } list)
            foreach (var e in list) PrintExperiment(e, false);
        return;
    }

    var r = await Call("RunExperiment", new { number = n });
    if (r.Success && Data<ExperimentResultDto>(r) is { } one) PrintExperiment(one, true);
}

void PrintExperiment(ExperimentResultDto e, bool withJournal)
{
    Console.WriteLine($"Эксперимент {e.Number}: {e.Title}");
    Console.WriteLine($"  доступно {e.AvailableNodes}/{e.TotalNodes}; ожидалось {e.Expected}; получено {e.Actual} — {(e.Passed ? "OK" : "ОШИБКА")}");
    if (withJournal) foreach (string line in e.Journal) Console.WriteLine($"    {line}");
}
