using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FlowerShop.Client;
using FlowerShop.Shared.Protocol;

// ===== ЛР №2. Клиентская часть: меню операций, формирование запросов, =====
// ===== асинхронная отправка серверу, получение ответов, вывод меток времени. =====
// ===== Клиент может запускаться несколько раз одновременно (несколько терминалов). =====

Console.OutputEncoding = Encoding.UTF8;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : ProtocolDefaults.DefaultPort;

Console.WriteLine("===============================================");
Console.WriteLine("  Заказ и доставка цветов — КЛИЕНТ (вариант 14)");
Console.WriteLine("             ЛР №2: многопоточность");
Console.WriteLine("===============================================");
Console.WriteLine($"Сервер: {host}:{port}");

using var client = new NetworkClient(host, port);

while (true)
{
    Console.WriteLine();
    Console.WriteLine("-------------- МЕНЮ ОПЕРАЦИЙ --------------");
    Console.WriteLine(" 1 — CreateOrder      (создать заказ)");
    Console.WriteLine(" 2 — CustomizeBouquet (собрать/изменить букет)");
    Console.WriteLine(" 3 — AssignFlorist    (назначить флориста)");
    Console.WriteLine(" 4 — ArrangeDelivery  (оформить доставку)");
    Console.WriteLine(" --------- Эксперименты ЛР №2 ---------");
    Console.WriteLine(" 5 — Медленный CreateOrder (задержка 10 с) — проверка параллелизма");
    Console.WriteLine(" 6 — ReserveBouquet   (бронь букета — Race Condition)");
    Console.WriteLine(" 7 — SetAvailability  (установить кол-во доступных букетов)");
    Console.WriteLine(" 8 — GetBouquetStatus (состояние общего ресурса)");
    Console.WriteLine(" 0 — Выход");
    Console.Write("Выберите операцию: ");

    string? choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "1": await DoCreateOrderAsync(); break;
            case "2": await DoCustomizeBouquetAsync(); break;
            case "3": await DoAssignFloristAsync(); break;
            case "4": await DoArrangeDeliveryAsync(); break;
            case "5": await DoSlowExperimentAsync(); break;
            case "6": await DoReserveBouquetAsync(); break;
            case "7": await DoSetAvailabilityAsync(); break;
            case "8": await DoGetStatusAsync(); break;
            case "0": Console.WriteLine("Клиент остановлен."); return;
            default: Console.WriteLine("Некорректная операция: выберите пункт меню 0–8."); break;
        }
    }
    catch (ServerUnavailableException ex)
    {
        // Требование 9: обработка ошибки "сервер недоступен"
        Console.WriteLine($"[ОШИБКА] Сервер недоступен: {ex.Message}");
        Console.WriteLine("Проверьте, запущен ли сервер (dotnet run --project Server), и попробуйте снова.");
    }
}

// ---------- Реализации пунктов меню ----------

async Task DoCreateOrderAsync()
{
    Console.WriteLine("--- CreateOrder: создание заказа ---");
    Console.WriteLine("Доступные клиенты и букеты (демо-данные):");
    Console.WriteLine("  Клиенты: 1 — Иванова Анна, 2 — Петров Сергей");
    Console.WriteLine("  Букеты:  1 — Розовое облако, 2 — Весенний, 3 — Новогодний люкс");

    int customerId = ReadInt("Id клиента: ");
    int bouquetId = ReadInt("Id букета: ");
    string address = ReadNonEmpty("Адрес доставки: ");
    string date = ReadDate("Дата доставки (ГГГГ-ММ-ДД): ");

    await SendAndPrintAsync(RequestBuilder.CreateOrder(customerId, bouquetId, address, date));
}

async Task DoSlowExperimentAsync()
{
    Console.WriteLine("--- Эксперимент: CreateOrder с задержкой обработки 10 с ---");
    Console.WriteLine("Задержка искусственно увеличивает время обработки запроса на сервере");
    Console.WriteLine("(Требование 4). Запустите ДВА клиента и отправьте этот запрос из каждого:");
    Console.WriteLine("если второй клиент завершится до первого — обработка параллельная.");
    Console.WriteLine();
    Console.WriteLine("  Клиенты: 1 — Иванова Анна, 2 — Петров Сергей");
    Console.WriteLine("  Букеты:  1 — Розовое облако, 2 — Весенний, 3 — Новогодний люкс");

    int customerId = ReadInt("Id клиента: ");
    int bouquetId = ReadInt("Id букета: ");
    string address = ReadNonEmpty("Адрес доставки: ");
    string date = ReadDate("Дата доставки (ГГГГ-ММ-ДД): ");

    const int experimentDelayMs = 10_000;
    Console.WriteLine($"\nЗадержка обработки: {experimentDelayMs} мс (10 с)");
    await SendAndPrintAsync(RequestBuilder.CreateOrder(customerId, bouquetId, address, date, experimentDelayMs));
}

async Task DoReserveBouquetAsync()
{
    Console.WriteLine("--- ReserveBouquet: бронь букета (эксперимент Race Condition) ---");
    Console.WriteLine("Демо-букеты: 1 — Розовое облако (3 шт.), 2 — Весенний (5 шт.), 3 — Новогодний люкс (1 шт.)");
    int bouquetId = ReadInt("Id букета: ");
    Console.Write("Задержка окна гонки, мс (Enter — 3000): ");
    string? delayRaw = Console.ReadLine()?.Trim();
    int? raceDelayMs = int.TryParse(delayRaw, out int d) ? d : null;
    if (raceDelayMs is not null && raceDelayMs < 0)
    {
        Console.WriteLine("Задержка не может быть отрицательной — используется 3000 мс.");
        raceDelayMs = null;
    }

    await SendAndPrintAsync(RequestBuilder.ReserveBouquet(bouquetId, raceDelayMs));
}

async Task DoSetAvailabilityAsync()
{
    Console.WriteLine("--- SetBouquetAvailability: установка количества ресурса ---");
    Console.WriteLine("Перед экспериментом установите количество = 1 (Требование 6).");
    Console.WriteLine("Демо-букеты: 1 — Розовое облако, 2 — Весенний, 3 — Новогодний люкс");
    int bouquetId = ReadInt("Id букета: ");
    int count = ReadInt("Новое количество (например, 1): ");

    await SendAndPrintAsync(RequestBuilder.SetBouquetAvailability(bouquetId, count));
}

async Task DoGetStatusAsync()
{
    Console.WriteLine("--- GetBouquetStatus: состояние общего ресурса ---");
    Console.Write("Id букета (Enter — показать все): ");
    string? raw = Console.ReadLine()?.Trim();
    int? bouquetId = int.TryParse(raw, out int id) ? id : null;

    await SendAndPrintAsync(RequestBuilder.GetBouquetStatus(bouquetId));
}

async Task DoCustomizeBouquetAsync()
{
    Console.WriteLine("--- CustomizeBouquet: сборка/изменение букета ---");
    int orderId = ReadInt("Id заказа: ");
    Console.Write("Новое название букета (Enter — оставить): ");
    string? name = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(name)) name = null;

    object[]? flowers = null;
    Console.Write("Заменить состав букета? (y/n): ");
    if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
    {
        var list = new List<object>();
        while (true)
        {
            Console.Write("  Цветок (Enter — закончить): ");
            string flower = Console.ReadLine()?.Trim() ?? "";
            if (flower.Length == 0) break;
            int count = ReadInt("  Количество: ");
            decimal price = ReadDecimal("  Цена за штуку: ");
            list.Add(new { flowerName = flower, count, pricePerUnit = price });
        }
        flowers = list.ToArray();
    }

    if (name is null && flowers is null)
    {
        Console.WriteLine("Нечего изменять: не указано ни название, ни состав.");
        return;
    }

    await SendAndPrintAsync(RequestBuilder.CustomizeBouquet(orderId, name, flowers));
}

async Task DoAssignFloristAsync()
{
    Console.WriteLine("--- AssignFlorist: назначение флориста ---");
    int orderId = ReadInt("Id заказа: ");
    Console.WriteLine("Флористы (демо-данные): 1 — Смирнова Ольга, 2 — Кузнецова Мария");
    int floristId = ReadInt("Id флориста: ");

    await SendAndPrintAsync(RequestBuilder.AssignFlorist(orderId, floristId));
}

async Task DoArrangeDeliveryAsync()
{
    Console.WriteLine("--- ArrangeDelivery: оформление доставки ---");
    int orderId = ReadInt("Id заказа: ");
    Console.Write("Новый адрес доставки (Enter — оставить из заказа): ");
    string? address = Console.ReadLine()?.Trim();
    Console.Write("Время доставки ГГГГ-ММ-ДД ЧЧ:ММ (Enter — оставить): ");
    string? plannedAt = Console.ReadLine()?.Trim();

    await SendAndPrintAsync(RequestBuilder.ArrangeDelivery(
        orderId,
        string.IsNullOrEmpty(address) ? null : address,
        string.IsNullOrEmpty(plannedAt) ? null : plannedAt));
}

/// <summary>
/// Асинхронная отправка запроса и вывод ответа сервера с временными метками
/// (Требование 3: время отправки и получения на стороне клиента).
/// </summary>
async Task SendAndPrintAsync(Request request)
{
    var stopwatch = Stopwatch.StartNew();
    DateTime sentAt = DateTime.Now;
    Console.WriteLine($"[{sentAt:HH:mm:ss.fff}] → запрос '{request.Operation}' отправлен...");

    Response response = await client.SendAsync(request);

    DateTime receivedAt = DateTime.Now;
    stopwatch.Stop();
    Console.WriteLine($"[{receivedAt:HH:mm:ss.fff}] ← ответ получен через {stopwatch.Elapsed.TotalSeconds:0.000} с");

    if (response.Success)
    {
        Console.WriteLine($"[OK] {response.Message}");
        if (response.Data is not null)
        {
            Console.WriteLine("Данные ответа:");
            Console.WriteLine(JsonSerializer.Serialize(response.Data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    else
    {
        // Требование 9: обработка ответов с ошибками
        Console.WriteLine($"[ОШИБКА {response.ErrorCode}] {response.Message}");
    }
}

int ReadInt(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        if (int.TryParse(Console.ReadLine()?.Trim(), out int value) && value > 0) return value;
        Console.WriteLine("  Введите целое положительное число.");
    }
}

decimal ReadDecimal(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        string? raw = Console.ReadLine()?.Trim().Replace(',', '.');
        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out decimal value) && value >= 0)
        {
            return value;
        }
        Console.WriteLine("  Введите неотрицательное число, например 120 или 99.50");
    }
}

string ReadNonEmpty(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        string? s = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(s)) return s;
        Console.WriteLine("  Значение не может быть пустым.");
    }
}

string ReadDate(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        string? s = Console.ReadLine()?.Trim();
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
        {
            return s!;
        }
        Console.WriteLine("  Введите дату в формате ГГГГ-ММ-ДД, например 2026-09-30");
    }
}
