using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FlowerShop.Client;
using FlowerShop.Shared.Protocol;

// ===== ЛР №5. Клиентская часть =====
// Продолжение ЛР №1–ЛР №4: предметная область «Заказ и доставка цветов» (вариант 14).
// Операции ЛР №5: репликация данных (Primary → Replica), обнаружение отказа и Failover,
// восстановление Primary (Recovery) и синхронизация копий.
// Пункты меню 18–24; предыдущие лабораторные сохранены (1–17) и продолжают работать.

Console.OutputEncoding = Encoding.UTF8;

string host = "127.0.0.1";
int port = ProtocolDefaults.DefaultPort;
TimeSpan clockOffset = TimeSpan.Zero;   // искусственное смещение часов клиента (Требование 1)

foreach (string arg in args)
{
    if (arg.StartsWith("--offset=", StringComparison.OrdinalIgnoreCase))
    {
        string raw = arg["--offset=".Length..].TrimEnd('s', 'S');
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            clockOffset = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            Console.WriteLine($"Некорректное смещение часов: '{arg}'. Пример: --offset=+7");
            return;
        }
    }
    else if (int.TryParse(arg, out int parsedPort))
    {
        port = parsedPort;
    }
    else
    {
        host = arg;
    }
}

// Часы клиента: LocalTime = UtcNow + ClockOffset (модель рассинхронизации).
var clock = new ClientClock { ClockOffset = clockOffset };

Console.WriteLine("==================================================");
Console.WriteLine("  Заказ и доставка цветов — КЛИЕНТ (вариант 14)");
Console.WriteLine("  ЛР №5: репликация данных и отказоустойчивость");
Console.WriteLine("==================================================");
Console.WriteLine($"Сервер: {host}:{port}");
Console.WriteLine($"Смещение часов клиента (модель): {FormatSigned(clock.ClockOffset)}");
Console.WriteLine($"Локальное время клиента: {FormatTime(clock.LocalNow)} UTC");

using var client = new NetworkClient(host, port);
var timeSync = new TimeSynchronizer(client, clock);

while (true)
{
    Console.WriteLine();
    Console.WriteLine("-------------- МЕНЮ ОПЕРАЦИЙ --------------");
    Console.WriteLine(" 1 — CreateOrder      (создать заказ)");
    Console.WriteLine(" 2 — CustomizeBouquet (собрать/изменить букет)");
    Console.WriteLine(" 3 — AssignFlorist    (назначить флориста)");
    Console.WriteLine(" 4 — ArrangeDelivery  (оформить доставку)");
    Console.WriteLine(" ------- Общий ресурс (ЛР №2, ЛР №3) -------");
    Console.WriteLine(" 5 — Медленный CreateOrder (задержка 10 с) — проверка параллелизма");
    Console.WriteLine(" 6 — ReserveBouquet   (бронь букета: критическая секция → SUCCESS/FAIL)");
    Console.WriteLine(" 7 — SetAvailability  (установить кол-во доступных букетов)");
    Console.WriteLine(" 8 — GetBouquetStatus (состояние общего ресурса)");
    Console.WriteLine(" ------- Синхронизация времени (ЛР №3) -------");
    Console.WriteLine(" 9 — GetServerTime    (локальное время клиента vs время сервера)");
    Console.WriteLine("10 — SyncTime         (RTT, поправка, скорректированное время)");
    Console.WriteLine("11 — SyncTime с задержкой сервера (влияние задержки на точность)");
    Console.WriteLine("12 — Проверить скорректированное время (остаточная ошибка)");
    Console.WriteLine("13 — Изменить смещение часов клиента (моделирование)");
    Console.WriteLine("14 — Итоговый эксперимент ЛР №3 (проверки 1–3)");
    Console.WriteLine(" ------- База данных (ЛР №4) -------");
    Console.WriteLine("15 — PlaceOrderAndReserve (составная операция: заказ + резерв)");
    Console.WriteLine("16 — ListOrders        (чтение списка заказов из БД)");
    Console.WriteLine("17 — Итоговый эксперимент ЛР №4 (без транзакции / COMMIT / ROLLBACK)");
    Console.WriteLine(" ------- Репликация и отказоустойчивость (ЛР №5) -------");
    Console.WriteLine("18 — GetInstanceState  (состояние Primary и Replica)");
    Console.WriteLine("19 — CheckConsistency  (проверка согласованности Primary = Replica)");
    Console.WriteLine("20 — Изменить данные в Primary (SetAvailability → репликация)");
    Console.WriteLine("21 — Смоделировать отказ Primary (Primary → UNAVAILABLE)");
    Console.WriteLine("22 — Failover          (переключение на Replica)");
    Console.WriteLine("23 — Восстановить Primary + синхронизация (Recover)");
    Console.WriteLine("24 — Итоговый эксперимент ЛР №5 (полный цикл отказоустойчивости)");
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
            case "9": await DoCompareTimeAsync(); break;
            case "10": await DoSyncTimeAsync(); break;
            case "11": await DoSyncWithDelayAsync(); break;
            case "12": await DoVerifyCorrectedTimeAsync(); break;
            case "13": DoChangeClockOffset(); break;
            case "14": await DoFinalExperimentAsync(); break;
            case "15": await DoPlaceOrderAndReserveAsync(); break;
            case "16": await DoListOrdersAsync(); break;
            case "17": await DoLab4FinalExperimentAsync(); break;
            case "18": await DoGetInstanceStateAsync(); break;
            case "19": await DoCheckConsistencyAsync(); break;
            case "20": await DoReplicatedUpdateAsync(); break;
            case "21": await DoSimulatePrimaryFailureAsync(); break;
            case "22": await DoFailoverAsync(); break;
            case "23": await DoRecoverAsync(); break;
            case "24": await DoLab5FinalExperimentAsync(); break;
            case "0": Console.WriteLine("Клиент остановлен."); return;
            default: Console.WriteLine("Некорректная операция: выберите пункт меню 0–24."); break;
        }
    }
    catch (ServerUnavailableException ex)
    {
        // Требование 11: обработка ошибки «сервер недоступен»
        Console.WriteLine($"[ОШИБКА] Сервер недоступен: {ex.Message}");
        Console.WriteLine("Проверьте, запущен ли сервер (dotnet run --project Server), и попробуйте снова.");
    }
    catch (TimeSynchronizationException ex)
    {
        // Требование 11: обработка ошибки «некорректные данные времени»
        Console.WriteLine($"[ОШИБКА {ex.ErrorCode ?? ErrorCodes.TimeSyncError}] {ex.Message}");
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
    Console.WriteLine("(Требование 4 ЛР №2). Запустите ДВА клиента и отправьте этот запрос из каждого:");
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
    Console.WriteLine("--- ReserveBouquet: бронирование букета (общий ресурс) ---");
    Console.WriteLine("ЛР №3: операция защищена критической секцией — при одном свободном букете");
    Console.WriteLine("успех (SUCCESS) получит только один клиент, второй получит отказ (FAIL).");
    Console.WriteLine("Демо-букеты: 1 — Розовое облако (3 шт.), 2 — Весенний (5 шт.), 3 — Новогодний люкс (1 шт.)");
    int bouquetId = ReadInt("Id букета: ");
    Console.Write("Задержка внутри критической секции, мс (Enter — 3000): ");
    string? delayRaw = Console.ReadLine()?.Trim();
    int? criticalDelayMs = int.TryParse(delayRaw, out int d) ? d : null;
    if (criticalDelayMs is not null && criticalDelayMs < 0)
    {
        Console.WriteLine("Задержка не может быть отрицательной — используется 3000 мс.");
        criticalDelayMs = null;
    }

    await SendAndPrintAsync(RequestBuilder.ReserveBouquet(bouquetId, criticalDelayMs));
}

async Task DoSetAvailabilityAsync()
{
    Console.WriteLine("--- SetBouquetAvailability: установка количества ресурса ---");
    Console.WriteLine("Перед экспериментом установите количество = 1, чтобы показать взаимное исключение.");
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

// ---------- ЛР №3, часть 2: синхронизация времени ----------

async Task DoCompareTimeAsync()
{
    Console.WriteLine("--- Сравнение локального времени клиента и времени сервера ---");
    Console.WriteLine("Демонстрация проблемы: часы разных узлов показывают разное время (Требование 2).");

    DateTime localBefore = clock.LocalNow;
    Response response = await client.SendAsync(RequestBuilder.GetServerTime());
    DateTime localAfter = clock.LocalNow;

    if (!response.Success)
    {
        Console.WriteLine($"[ОШИБКА {response.ErrorCode}] {response.Message}");
        return;
    }

    DateTime serverTime = TimeSynchronizer.ParseServerTime(response);

    Console.WriteLine($"  Время сервера (UTC):          {FormatTime(serverTime)}");
    Console.WriteLine($"  Локальное время клиента:      {FormatTime(localAfter)}");
    Console.WriteLine($"  Смещение часов клиента:       {FormatSigned(clock.ClockOffset)} (модель рассинхронизации)");
    Console.WriteLine($"  Разница Local − Server:       {FormatSigned(localAfter - serverTime)}");
    Console.WriteLine("  (точная поправка вычисляется в пункте 10 с учётом задержки RTT)");
}

async Task DoSyncTimeAsync()
{
    Console.WriteLine("--- SyncTime: синхронизация времени клиента с сервером ---");
    Console.WriteLine("Схема: t1 → TIME_REQUEST → сервер фиксирует serverTime → TIME_RESPONSE → t4.");
    Console.WriteLine("       RTT = t4 − t1;  Offset = serverTime − (t1 + RTT/2).");

    TimeSyncResult result = await timeSync.SyncAsync();
    PrintSyncResult(result);

    TimeSpan residual = await timeSync.MeasureResidualErrorAsync();
    Console.WriteLine($"  Разница после синхронизации (остаточная ошибка): {FormatSigned(residual)}");
}

async Task DoSyncWithDelayAsync()
{
    Console.WriteLine("--- SyncTime с искусственной задержкой сервера (Требование 7) ---");
    Console.WriteLine("Задержка сдвигает момент фиксации времени сервера и нарушает симметрию обмена.");
    Console.Write("  Задержка сервера перед фиксацией времени, мс (Enter — 1000): ");
    string? raw = Console.ReadLine()?.Trim();
    int serverDelayMs = int.TryParse(raw, out int d) && d >= 0 ? d : 1000;

    TimeSyncResult result = await timeSync.SyncAsync(serverDelayMs);
    PrintSyncResult(result);

    TimeSpan residual = await timeSync.MeasureResidualErrorAsync();
    Console.WriteLine($"  Разница после синхронизации (остаточная ошибка): {FormatSigned(residual)}");
    Console.WriteLine("  Вывод: чем больше задержка, тем ниже точность — поправка смещается");
    Console.WriteLine("  примерно на delay/2, потому что оценка RTT/2 не компенсирует задержку");
    Console.WriteLine("  обработки на сервере.");
}

async Task DoVerifyCorrectedTimeAsync()
{
    Console.WriteLine("--- Проверка скорректированного времени ---");
    if (clock.IsSynchronized)
    {
        Console.WriteLine($"  Сохранённая поправка (Correction): {FormatSigned(clock.Correction)}");
    }
    else
    {
        Console.WriteLine("  Время ещё не синхронизировано — сначала выполните пункт 10.");
    }

    DateTime local = clock.LocalNow;
    DateTime corrected = clock.CorrectedNow;
    Response response = await client.SendAsync(RequestBuilder.GetServerTime());

    if (!response.Success)
    {
        Console.WriteLine($"[ОШИБКА {response.ErrorCode}] {response.Message}");
        return;
    }

    DateTime serverTime = TimeSynchronizer.ParseServerTime(response);

    Console.WriteLine($"  Локальное время клиента:      {FormatTime(local)} UTC");
    Console.WriteLine($"  Скорректированное время:      {FormatTime(corrected)} UTC");
    Console.WriteLine($"  Время сервера (новый замер):  {FormatTime(serverTime)} UTC");
    Console.WriteLine($"  Остаточная разница Corrected − Server: {FormatSigned(corrected - serverTime)}");
}

void DoChangeClockOffset()
{
    Console.WriteLine("--- Моделирование рассинхронизации: смещение часов клиента ---");
    Console.WriteLine($"  Текущее смещение часов клиента: {FormatSigned(clock.ClockOffset)}");
    Console.Write("  Новое смещение в секундах, например +7 или -5 (Enter — 0): ");
    string? raw = Console.ReadLine()?.Trim();

    double seconds = 0;
    if (!string.IsNullOrEmpty(raw))
    {
        string normalized = raw.TrimStart('+').TrimEnd('s', 'S').Replace(',', '.');
        double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    clock.ClockOffset = TimeSpan.FromSeconds(seconds);
    clock.ResetCorrection();   // при новом смещении прежняя поправка недействительна

    Console.WriteLine($"  Новое смещение часов клиента:  {FormatSigned(clock.ClockOffset)}");
    Console.WriteLine($"  Локальное время клиента:       {FormatTime(clock.LocalNow)} UTC");
    Console.WriteLine("  Поправка сброшена — выполните синхронизацию (пункт 10) заново.");
}

async Task DoFinalExperimentAsync()
{
    Console.WriteLine("=============== ИТОГОВЫЙ ЭКСПЕРИМЕНТ ЛР №3 ===============");

    // ---------- Проверка 1: общий ресурс ----------
    Console.WriteLine();
    Console.WriteLine("[Проверка 1] Общий ресурс: 1 свободный букет и две одновременные заявки.");
    Console.WriteLine("NetworkClient открывает отдельное соединение на каждый запрос, поэтому");
    Console.WriteLine("сервер обрабатывает заявки как двух независимых клиентов в параллельных Task.");

    Response set = await client.SendAsync(RequestBuilder.SetBouquetAvailability(3, 1));
    Console.WriteLine($"  Подготовка: {set.Message}");

    var stopwatch = Stopwatch.StartNew();
    Task<Response> claim1 = client.SendAsync(RequestBuilder.ReserveBouquet(3, criticalDelayMs: 1500));
    await Task.Delay(200);
    Task<Response> claim2 = client.SendAsync(RequestBuilder.ReserveBouquet(3, criticalDelayMs: 1500));
    Response[] claims = await Task.WhenAll(claim1, claim2);
    stopwatch.Stop();

    Console.WriteLine($"  Заявка 1: {(claims[0].Success ? "SUCCESS" : $"FAIL ({claims[0].ErrorCode})")} — {claims[0].Message}");
    Console.WriteLine($"  Заявка 2: {(claims[1].Success ? "SUCCESS" : $"FAIL ({claims[1].ErrorCode})")} — {claims[1].Message}");
    Console.WriteLine($"  Успешных бронирований: {claims.Count(r => r.Success)} из 2 (при 1 свободном букете ожидается ровно 1)");
    Console.WriteLine($"  Общее время обеих заявок: {stopwatch.Elapsed.TotalSeconds:0.000} с (заявки параллельны)");

    Response status = await client.SendAsync(RequestBuilder.GetBouquetStatus(3));
    Console.WriteLine($"  Состояние ресурса после эксперимента: {status.Message}");

    // ---------- Проверка 2: синхронизация времени ----------
    Console.WriteLine();
    Console.WriteLine("[Проверка 2] Синхронизация времени");
    Console.WriteLine($"  Смещение часов клиента (модель): {FormatSigned(clock.ClockOffset)}");
    Console.WriteLine($"  Локальное время клиента ДО синхронизации: {FormatTime(clock.LocalNow)} UTC");

    TimeSyncResult sync = await timeSync.SyncAsync();
    PrintSyncResult(sync);
    TimeSpan residual = await timeSync.MeasureResidualErrorAsync();
    Console.WriteLine($"  Разница ПОСЛЕ синхронизации (остаточная ошибка): {FormatSigned(residual)}");

    // ---------- Проверка 3: влияние задержки ----------
    Console.WriteLine();
    Console.WriteLine("[Проверка 3] Синхронизация при искусственной задержке сервера 1000 мс");
    TimeSyncResult delayed = await timeSync.SyncAsync(serverDelayMs: 1000);
    PrintSyncResult(delayed);
    TimeSpan delayedResidual = await timeSync.MeasureResidualErrorAsync();
    Console.WriteLine($"  Разница ПОСЛЕ синхронизации (остаточная ошибка): {FormatSigned(delayedResidual)}");
    Console.WriteLine("  Вывод: задержка сервера нарушает симметрию обмена, и оценка RTT/2 перестаёт");
    Console.WriteLine("  компенсировать задержку обработки — точность падает (ошибка ≈ delay/2).");

    Console.WriteLine();
    Console.WriteLine("Значения выше можно перенести в Таблицу 2 отчёта по ЛР №3.");
}

// ---------- Операции ЛР №4 (база данных и транзакции) ----------

async Task DoPlaceOrderAndReserveAsync()
{
    Console.WriteLine("--- PlaceOrderAndReserve: составная операция «заказ + резерв букета» ---");
    Console.WriteLine("Операции: 1) создание заказа; 2) уменьшение ресурса букета (Bouquets.AvailableCount).");
    Console.WriteLine("С транзакцией обе операции фиксируются COMMIT или отменяются ROLLBACK целиком.");
    Console.WriteLine("  Клиенты: 1 — Иванова Анна, 2 — Петров Сергей");
    Console.WriteLine("  Букеты:  1 — Розовое облако, 2 — Весенний, 3 — Новогодний люкс");

    int customerId = ReadInt("Id клиента: ");
    int bouquetId = ReadInt("Id букета: ");
    string address = ReadNonEmpty("Адрес доставки: ");
    string date = ReadDate("Дата доставки (ГГГГ-ММ-ДД): ");

    Console.Write("Использовать транзакцию? (Y/n, по умолчанию Y): ");
    bool useTransaction = !string.Equals(Console.ReadLine()?.Trim(), "n", StringComparison.OrdinalIgnoreCase);
    Console.Write("Искусственно вызвать ошибку между операциями? (y/N): ");
    bool simulatedError = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

    if (useTransaction)
    {
        Console.WriteLine("Режим: BEGIN → Операция 1 → Операция 2 → COMMIT (при ошибке — ROLLBACK).");
    }
    else
    {
        Console.WriteLine("Режим БЕЗ транзакции: автокоммит; при ошибке между операциями возможны частичные изменения.");
    }

    await SendAndPrintAsync(RequestBuilder.PlaceOrderAndReserve(customerId, bouquetId, address, date, useTransaction, simulatedError));
}

async Task DoListOrdersAsync()
{
    Console.WriteLine("--- ListOrders: чтение списка заказов из БД ---");
    await SendAndPrintAsync(RequestBuilder.ListOrders());
}

async Task DoLab4FinalExperimentAsync()
{
    Console.WriteLine("================ ИТОГОВЫЙ ЭКСПЕРИМЕНТ ЛР №4 ================");
    Console.WriteLine("Составная операция «создать заказ + зарезервировать букет» над БД MSSQL.");
    Console.WriteLine("Букет #3 «Новогодний люкс» — общий ресурс, количество будет установлено в 1.");
    Console.WriteLine();

    // ---------- Подготовка: ресурс = 1, счётчик заказов ----------
    Response setStatus = await SendRawAsync(RequestBuilder.SetBouquetAvailability(3, 1));
    Console.WriteLine($"  Подготовка: {setStatus.Message}");

    Response beforeList = await SendRawAsync(RequestBuilder.ListOrders());
    int ordersBefore = beforeList.Success && beforeList.Data is JsonElement d && d.TryGetProperty("count", out var c)
        ? c.GetInt32() : -1;
    Console.WriteLine($"  Заказов в БД до эксперимента: {ordersBefore}");
    Console.WriteLine();

    // ---------- Эксперимент 1: без транзакции + ошибка → частичное изменение ----------
    Console.WriteLine("[Эксперимент 1] БЕЗ транзакции + искусственная ошибка между операциями");
    Console.WriteLine("  Ожидание: Операция 1 (INSERT заказа) уже автокоммичена, ошибка случится в Операции 2 —");
    Console.WriteLine("  заказ ОСТАЁТСЯ в БД, ресурс НЕ уменьшен → частичное изменение данных.");
    Response exp1 = await SendRawAsync(RequestBuilder.PlaceOrderAndReserve(1, 3, "ул. Транзакционная, 1", "2026-10-20",
        useTransaction: false, simulatedError: true));
    PrintExpResult(exp1);
    await PrintDbStateAsync();
    Console.WriteLine();

    // ---------- Эксперимент 2: транзакция + ошибка → ROLLBACK ----------
    Console.WriteLine("[Эксперимент 2] ТРАНЗАКЦИЯ + искусственная ошибка между операциями");
    Console.WriteLine("  Ожидание: BEGIN → Операция 1 → Ошибка → ROLLBACK → в БД НИЧЕГО не изменилось.");
    Response exp2 = await SendRawAsync(RequestBuilder.PlaceOrderAndReserve(1, 3, "ул. Транзакционная, 2", "2026-10-21",
        useTransaction: true, simulatedError: true));
    PrintExpResult(exp2);
    await PrintDbStateAsync();
    Console.WriteLine();

    // ---------- Эксперимент 3: транзакция без ошибки → COMMIT ----------
    Console.WriteLine("[Эксперимент 3] ТРАНЗАКЦИЯ без ошибок");
    Console.WriteLine("  Ожидание: BEGIN → Операция 1 → Операция 2 → COMMIT → заказ добавлен, ресурс уменьшен.");
    Response exp3 = await SendRawAsync(RequestBuilder.PlaceOrderAndReserve(1, 3, "ул. Транзакционная, 3", "2026-10-22",
        useTransaction: true, simulatedError: false));
    PrintExpResult(exp3);
    await PrintDbStateAsync();
    Console.WriteLine();

    Console.WriteLine("Сравнение (Таблица 5 отчёта):");
    Console.WriteLine("  • Эксперимент 1 (без транзакции): заказ в БД остался — частичное изменение.");
    Console.WriteLine("  • Эксперимент 2 (ROLLBACK): БД не изменилась — согласованность сохранена.");
    Console.WriteLine("  • Эксперимент 3 (COMMIT): обе операции зафиксированы атомарно.");
}

void PrintExpResult(Response r)
{
    if (r.Success)
    {
        Console.WriteLine($"  Результат: SUCCESS — {r.Message}");
    }
    else
    {
        Console.WriteLine($"  Результат: FAIL [{r.ErrorCode}] — {r.Message}");
    }
}

async Task PrintDbStateAsync()
{
    Response status = await SendRawAsync(RequestBuilder.GetBouquetStatus(3));
    Console.WriteLine($"  Ресурс:   {status.Message}");
    Response list = await SendRawAsync(RequestBuilder.ListOrders());
    Console.WriteLine($"  Заказы:   {list.Message}");
}

/// <summary>
/// Отправка запроса без декоративной печати (для пошагового эксперимента ЛР №4).
/// </summary>
async Task<Response> SendRawAsync(Request request)
{
    try
    {
        return await client.SendAsync(request);
    }
    catch (ServerUnavailableException ex)
    {
        return Response.Fail(ErrorCodes.ServerUnavailable, ex.Message);
    }
}

// ---------- Операции ЛР №5: репликация и отказоустойчивость ----------

async Task DoGetInstanceStateAsync()
{
    Console.WriteLine("--- Состояние экземпляров Primary и Replica (Требование 5 ч.1) ---");
    Response response = await SendRawAsync(RequestBuilder.GetInstanceState());
    PrintData(response);
}

async Task DoCheckConsistencyAsync()
{
    Console.WriteLine("--- Проверка согласованности Primary = Replica (Требование 7 ч.1) ---");
    Response response = await SendRawAsync(RequestBuilder.CheckConsistency());
    PrintData(response);
}

async Task DoReplicatedUpdateAsync()
{
    Console.WriteLine("--- Изменение данных в Primary с репликацией в Replica (Требование 6 ч.1) ---");
    Console.WriteLine("Операция SetBouquetAvailability изменяет общий ресурс в активном экземпляре,");
    Console.WriteLine("после чего сервер передаёт изменение в резервный экземпляр (Replication).");
    Console.WriteLine("Демо-букеты: 1 — Розовое облако, 2 — Весенний, 3 — Новогодний люкс");
    int bouquetId = ReadInt("Id букета: ");
    int count = ReadInt("Новое количество: ");

    Console.WriteLine("\n[до операции]");
    PrintData(await SendRawAsync(RequestBuilder.GetBouquetStatus(bouquetId)));

    await SendAndPrintAsync(RequestBuilder.SetBouquetAvailability(bouquetId, count));

    Console.WriteLine("[после операции]");
    PrintData(await SendRawAsync(RequestBuilder.GetBouquetStatus(bouquetId)));
    PrintData(await SendRawAsync(RequestBuilder.CheckConsistency()));
}

async Task DoSimulatePrimaryFailureAsync()
{
    Console.WriteLine("--- Моделирование отказа Primary (Требование 2 ч.3) ---");
    Console.WriteLine("Primary переводится в состояние UNAVAILABLE.");
    PrintData(await SendRawAsync(RequestBuilder.SetInstanceState("primary", false)));
    PrintData(await SendRawAsync(RequestBuilder.GetInstanceState()));
}

async Task DoFailoverAsync()
{
    Console.WriteLine("--- Failover: переключение на Replica (Требование 3 ч.3) ---");
    PrintData(await SendRawAsync(RequestBuilder.Failover()));
    PrintData(await SendRawAsync(RequestBuilder.GetInstanceState()));
    Console.WriteLine("Теперь запросы обрабатываются через резервный экземпляр (Replica).");
}

async Task DoRecoverAsync()
{
    Console.WriteLine("--- Восстановление Primary и синхронизация (Требования 1–2 ч.4) ---");
    PrintData(await SendRawAsync(RequestBuilder.Recover()));
    PrintData(await SendRawAsync(RequestBuilder.GetInstanceState()));
}

/// <summary>
/// Итоговый эксперимент ЛР №5 (Требование 3 ч.4): полный цикл отказоустойчивости.
///  1. Primary AVAILABLE, Replica AVAILABLE, начальное совпадение;
///  2. изменение в Primary → репликация → Primary = Replica;
///  3. отказ Primary → обнаружение → Failover на Replica;
///  4. запрос обрабатывается через Replica;
///  5. восстановление Primary → синхронизация → Primary = Replica.
/// </summary>
async Task DoLab5FinalExperimentAsync()
{
    Console.WriteLine("=====================================================");
    Console.WriteLine("  Итоговый эксперимент ЛР №5: полный цикл");
    Console.WriteLine("  Primary → Replication → Failover → Recovery");
    Console.WriteLine("=====================================================");

    int bouquetId = ReadInt("Id букета для эксперимента (напр. 2): ");
    int startCount = ReadInt("Начальное количество ресурса (напр. 5): ");

    Console.WriteLine("\n[Шаг 1] Штатное состояние. Установка ресурса в Primary и репликация.");
    PrintData(await SendRawAsync(RequestBuilder.SetBouquetAvailability(bouquetId, startCount)));
    PrintData(await SendRawAsync(RequestBuilder.CheckConsistency()));

    Console.WriteLine("\n[Шаг 2] Изменение в Primary → Replication → проверка согласованности.");
    PrintData(await SendRawAsync(RequestBuilder.SetBouquetAvailability(bouquetId, startCount - 1)));
    PrintData(await SendRawAsync(RequestBuilder.CheckConsistency()));

    Console.WriteLine("\n[Шаг 3] Моделирование отказа Primary (Primary → UNAVAILABLE).");
    PrintData(await SendRawAsync(RequestBuilder.SetInstanceState("primary", false)));
    PrintData(await SendRawAsync(RequestBuilder.GetInstanceState()));

    Console.WriteLine("\n[Шаг 4] Failover на Replica и запрос ЧЕРЕЗ РЕЗЕРВНЫЙ экземпляр.");
    PrintData(await SendRawAsync(RequestBuilder.Failover()));
    PrintData(await SendRawAsync(RequestBuilder.GetBouquetStatus(bouquetId)));
    PrintData(await SendRawAsync(RequestBuilder.SetBouquetAvailability(bouquetId, startCount - 2)));

    Console.WriteLine("\n[Шаг 5] Восстановление Primary (Recovery) и синхронизация.");
    PrintData(await SendRawAsync(RequestBuilder.Recover()));
    PrintData(await SendRawAsync(RequestBuilder.CheckConsistency()));

    Console.WriteLine("\nГотово. Ожидаемый итог: на шагах 1–2 и после шага 5 — Primary = Replica;");
    Console.WriteLine("на шаге 4 запрос успешно обработан через Replica, хотя Primary был недоступен.");
}

/// <summary>Вывод результата операции: сообщение и данные ответа (для ЛР №5).</summary>
void PrintData(Response response)
{
    if (response.Success)
    {
        Console.WriteLine($"[OK] {response.Message}");
        if (response.Data is not null)
        {
            Console.WriteLine(JsonSerializer.Serialize(response.Data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    else
    {
        Console.WriteLine($"[ОШИБКА {response.ErrorCode}] {response.Message}");
    }
}

// ---------- Операции ЛР №1 (без изменений) ----------

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

// ---------- Вспомогательные функции ----------

/// <summary>
/// Асинхронная отправка запроса и вывод ответа сервера с временными метками
/// (Требование 3 ЛР №2: время отправки и получения на стороне клиента).
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
        // Требование 11: обработка ответов с ошибками
        Console.WriteLine($"[ОШИБКА {response.ErrorCode}] {response.Message}");
    }
}

/// <summary>Вывод результата синхронизации времени (Требования 4–8 ЛР №3, часть 2).</summary>
void PrintSyncResult(TimeSyncResult r)
{
    Console.WriteLine($"  Время отправки запроса t1:                {FormatTime(r.LocalBefore)} (локальное)");
    Console.WriteLine($"  Время сервера serverTime:                 {FormatTime(r.ServerTime)} UTC");
    Console.WriteLine($"  RTT (полное время обмена):                {r.Rtt.TotalSeconds:0.000} с");
    Console.WriteLine($"  Оценка задержки в одну сторону (RTT/2):   {r.EstimatedNetworkDelay.TotalSeconds:0.000} с");
    if (r.ServerDelayMs > 0)
    {
        Console.WriteLine($"  Искусственная задержка сервера:           {r.ServerDelayMs} мс");
    }
    Console.WriteLine($"  Вычисленная поправка Offset:              {FormatSigned(r.CalculatedOffset)}");
    Console.WriteLine($"  Скорректированное время клиента:          {FormatTime(r.CorrectedTime)} UTC");
}

/// <summary>Форматирование времени для вывода.</summary>
string FormatTime(DateTime time) => time.ToString("HH:mm:ss.fff");

/// <summary>Форматирование знаковой величины (смещение, поправка, RTT).</summary>
string FormatSigned(TimeSpan value)
{
    string sign = value < TimeSpan.Zero ? "-" : "+";
    return $"{sign}{value.Duration().TotalSeconds:0.000} с";
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
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) && value >= 0)
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
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return s!;
        }
        Console.WriteLine("  Введите дату в формате ГГГГ-ММ-ДД, например 2026-09-30");
    }
}
