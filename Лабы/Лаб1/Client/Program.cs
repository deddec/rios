using System.Text;
using System.Text.Json;
using FlowerShop.Client;
using FlowerShop.Shared.Protocol;

// ===== ЛР №1. Клиентская часть: меню операций, формирование запросов, =====
// ===== отправка их серверу, получение ответов, отображение результатов. =====

Console.OutputEncoding = Encoding.UTF8;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : ProtocolDefaults.DefaultPort;

Console.WriteLine("===============================================");
Console.WriteLine("  Заказ и доставка цветов — КЛИЕНТ (вариант 14)");
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
    Console.WriteLine(" 0 — Выход");
    Console.Write("Выберите операцию: ");

    string? choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "1": DoCreateOrder(); break;
            case "2": DoCustomizeBouquet(); break;
            case "3": DoAssignFlorist(); break;
            case "4": DoArrangeDelivery(); break;
            case "0": Console.WriteLine("Клиент остановлен."); return;
            default: Console.WriteLine("Некорректная операция: выберите пункт меню 0–4."); break;
        }
    }
    catch (ServerUnavailableException ex)
    {
        // Требование 8: обработка ошибки "сервер недоступен"
        Console.WriteLine($"[ОШИБКА] Сервер недоступен: {ex.Message}");
        Console.WriteLine("Проверьте, запущен ли сервер (dotnet run --project Server), и попробуйте снова.");
    }
}

// ---------- Реализации пунктов меню ----------

void DoCreateOrder()
{
    Console.WriteLine("--- CreateOrder: создание заказа ---");
    Console.WriteLine("Доступные клиенты и букеты (демо-данные):");
    Console.WriteLine("  Клиенты: 1 — Иванова Анна, 2 — Петров Сергей");
    Console.WriteLine("  Букеты:  1 — Розовое облако, 2 — Весенний");

    int customerId = ReadInt("Id клиента: ");
    int bouquetId = ReadInt("Id букета: ");
    string address = ReadNonEmpty("Адрес доставки: ");
    string date = ReadDate("Дата доставки (ГГГГ-ММ-ДД): ");

    SendAndPrint(RequestBuilder.CreateOrder(customerId, bouquetId, address, date));
}

void DoCustomizeBouquet()
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

    SendAndPrint(RequestBuilder.CustomizeBouquet(orderId, name, flowers));
}


void DoAssignFlorist()
{
    Console.WriteLine("--- AssignFlorist: назначение флориста ---");
    int orderId = ReadInt("Id заказа: ");
    Console.WriteLine("Флористы (демо-данные): 1 — Смирнова Ольга, 2 — Кузнецова Мария");
    int floristId = ReadInt("Id флориста: ");

    SendAndPrint(RequestBuilder.AssignFlorist(orderId, floristId));
}

void DoArrangeDelivery()
{
    Console.WriteLine("--- ArrangeDelivery: оформление доставки ---");
    int orderId = ReadInt("Id заказа: ");
    Console.Write("Новый адрес доставки (Enter — оставить из заказа): ");
    string? address = Console.ReadLine()?.Trim();
    Console.Write("Время доставки ГГГГ-ММ-ДД ЧЧ:ММ (Enter — оставить): ");
    string? plannedAt = Console.ReadLine()?.Trim();

    SendAndPrint(RequestBuilder.ArrangeDelivery(
        orderId,
        string.IsNullOrEmpty(address) ? null : address,
        string.IsNullOrEmpty(plannedAt) ? null : plannedAt));
}

/// <summary>Отправка запроса и вывод ответа сервера.</summary>
void SendAndPrint(Request request)
{
    Response response = client.Send(request);
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
        // Требование 8: обработка ответов с ошибками (некорректная операция,
        // отсутствующий объект, некорректные данные и т.д.)
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
