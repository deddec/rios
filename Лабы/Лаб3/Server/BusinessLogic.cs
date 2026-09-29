using System.Text.Json;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>
/// Бизнес-логика предметной области "Заказ и доставка цветов" (вариант 14).
/// Операции ЛР №1: CreateOrder, CustomizeBouquet, AssignFlorist, ArrangeDelivery.
/// Операции ЛР №2: ReserveBouquet, SetBouquetAvailability, GetBouquetStatus.
/// Операции ЛР №3, часть 2: GetServerTime (TIME_REQUEST/TIME_RESPONSE).
///
/// ЛР №3, часть 1: доступ к общему ресурсу (<see cref="Bouquet.AvailableCount"/>)
/// защищён критической секцией. Механизм синхронизации выбирается полем
/// <see cref="Sync"/>: lock (основной), SemaphoreSlim (альтернативный) или
/// None (поведение ЛР №2 — для сравнения).
/// </summary>
public class BusinessLogic
{
    private readonly InMemoryDataStore _db;

    /// <summary>Объект синхронизации для lock (Требование 5): защищает критическую секцию.</summary>
    private readonly object _syncObject = new();

    /// <summary>
    /// Семафор на одно разрешение (Требование 9): альтернатива lock.
    /// При значении 1 семафор организует взаимное исключение; в отличие от lock
    /// его можно ожидать асинхронно (WaitAsync), не блокируя поток.
    /// </summary>
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Выбранный механизм синхронизации общего ресурса (ЛР №3).</summary>
    public SyncMechanism Sync { get; }

    public BusinessLogic(InMemoryDataStore? db = null, SyncMechanism sync = SyncMechanism.Lock)
    {
        _db = db ?? new InMemoryDataStore();
        Sync = sync;
    }

    /// <summary>Доступ к списку заказов для тестов и отладки.</summary>
    public IReadOnlyList<Order> DbgOrders => _db.Orders;

    /// <summary>
    /// Асинхронный диспетчер операций. ЛР №3: контекст запроса (<see cref="RequestContext"/>)
    /// позволяет операциям с общим ресурсом писать в журнал сервера (LOCK/UNLOCK/READ/…).
    /// Бросает OperationException при некорректной операции или данных.
    /// </summary>
    public Task<Response> ExecuteAsync(Request request, RequestContext? context = null, CancellationToken ct = default)
    {
        switch (request.Operation)
        {
            case "CreateOrder": return Task.FromResult(CreateOrder(request.Payload));
            case "CustomizeBouquet": return Task.FromResult(CustomizeBouquet(request.Payload));
            case "AssignFlorist": return Task.FromResult(AssignFlorist(request.Payload));
            case "ArrangeDelivery": return Task.FromResult(ArrangeDelivery(request.Payload));
            case "ReserveBouquet": return ReserveBouquetAsync(request.Payload, context, ct);
            case "SetBouquetAvailability": return SetBouquetAvailabilityAsync(request.Payload, context, ct);
            case "GetBouquetStatus": return Task.FromResult(GetBouquetStatus(request.Payload));
            // ЛР №3, часть 2: TIME_REQUEST / TIME_RESPONSE (GetServerTime + псевдоним TimeRequest)
            case "GetServerTime":
            case "TimeRequest": return GetServerTimeAsync(context);
            default:
                throw new OperationException(
                    ErrorCodes.UnknownOperation,
                    $"Неизвестная операция: '{request.Operation}'. Доступны: CreateOrder, CustomizeBouquet, " +
                    "AssignFlorist, ArrangeDelivery, ReserveBouquet, SetBouquetAvailability, GetBouquetStatus, GetServerTime");
        }
    }

    /// <summary>Синхронный диспетчер (обёртка над <see cref="ExecuteAsync"/>).</summary>
    public Response Execute(Request request, RequestContext? context = null) =>
        ExecuteAsync(request, context).GetAwaiter().GetResult();
    // ----- CreateOrder -------------------------------------------------

    public Response CreateOrder(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуются данные заказа");

        int customerId = GetInt(p, "customerId");
        int bouquetId = GetInt(p, "bouquetId");
        string address = GetString(p, "deliveryAddress");
        DateTime deliveryDate = GetDate(p, "deliveryDate");

        Customer? customer = _db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer is null)
        {
            throw new OperationException(ErrorCodes.NotFound, $"Клиент с Id={customerId} не найден");
        }

        Bouquet? bouquet = _db.Bouquets.FirstOrDefault(b => b.Id == bouquetId);
        if (bouquet is null)
        {
            throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден");
        }

        var order = new Order
        {
            Id = _db.NextOrderId(),
            CustomerId = customerId,
            BouquetId = bouquetId,
            DeliveryDate = deliveryDate,
            DeliveryAddress = address,
            Status = OrderStatus.Created
        };
        _db.Orders.Add(order);
        InMemoryDataStore.AddEvent(order, "OrderCreated");

        return Response.Ok(new { order.Id, order.Status, Bouquet = bouquet.Name, Total = bouquet.TotalPrice },
            $"Заказ #{order.Id} создан для {customer.FullName}. Сумма: {bouquet.TotalPrice} руб.");
    }

    // ----- CustomizeBouquet ---------------------------------------------

    public Response CustomizeBouquet(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуются данные букета");

        int orderId = GetInt(p, "orderId");
        Order? order = _db.Orders.FirstOrDefault(o => o.Id == orderId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Заказ с Id={orderId} не найден");

        if (order.Status == OrderStatus.Cancelled)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} отменён — изменение невозможно");
        }
        if (order.Status != OrderStatus.Created)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} уже в статусе '{order.Status}' — состав менять нельзя");
        }

        Bouquet? bouquet = _db.Bouquets.FirstOrDefault(b => b.Id == order.BouquetId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={order.BouquetId} не найден");

        // Замена состава букета, если передан список цветов
        if (p.TryGetProperty("flowers", out var flowersEl) && flowersEl.ValueKind == JsonValueKind.Array)
        {
            var items = new List<FlowerItem>();
            foreach (var f in flowersEl.EnumerateArray())
            {
                string name = GetString(f, "flowerName");
                int count = GetInt(f, "count");
                decimal price = GetDecimal(f, "pricePerUnit");
                items.Add(new FlowerItem { FlowerName = name, Count = count, PricePerUnit = price });
            }
            bouquet.Flowers = items;
        }

        // Изменение названия букета, если передано
        if (p.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            bouquet.Name = nameEl.GetString() ?? bouquet.Name;
        }

        if (bouquet.Flowers.Count == 0)
        {
            throw new OperationException(ErrorCodes.ValidationError, "Состав букета пуст — добавьте хотя бы один цветок");
        }

        order.Status = OrderStatus.BouquetPrepared;
        InMemoryDataStore.AddEvent(order, "BouquetPrepared");

        return Response.Ok(new { BouquetId = bouquet.Id, bouquet.Name, bouquet.Flowers, Total = bouquet.TotalPrice },
            $"Букет '{bouquet.Name}' собран. Сумма: {bouquet.TotalPrice} руб.");
    }

    // ----- AssignFlorist ------------------------------------------------

    public Response AssignFlorist(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуется Id заказа и флориста");

        int orderId = GetInt(p, "orderId");
        int floristId = GetInt(p, "floristId");

        Order? order = _db.Orders.FirstOrDefault(o => o.Id == orderId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Заказ с Id={orderId} не найден");
        if (order.Status == OrderStatus.Cancelled)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} отменён");
        }
        if (order.FloristId is not null)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"К заказу #{orderId} уже назначен флорист (Id={order.FloristId})");
        }

        Florist? florist = _db.Florists.FirstOrDefault(f => f.Id == floristId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Флорист с Id={floristId} не найден");
        if (florist.IsBusy)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Флорист {florist.FullName} сейчас занят");
        }

        order.FloristId = floristId;
        order.Status = OrderStatus.FloristAssigned;
        florist.IsBusy = true;
        InMemoryDataStore.AddEvent(order, "FloristAssigned");

        return Response.Ok(new { order.Id, FloristId = florist.Id, Florist = florist.FullName },
            $"Флорист {florist.FullName} назначен на заказ #{order.Id}");
    }

    // ----- ArrangeDelivery ----------------------------------------------

    public Response ArrangeDelivery(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуется Id заказа");

        int orderId = GetInt(p, "orderId");
        Order? order = _db.Orders.FirstOrDefault(o => o.Id == orderId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Заказ с Id={orderId} не найден");
        if (order.Status == OrderStatus.Cancelled)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} отменён — доставка невозможна");
        }
        if (order.Status != OrderStatus.FloristAssigned)
        {
            throw new OperationException(ErrorCodes.ValidationError,
                $"Нельзя оформить доставку для заказа #{orderId}: сначала назначьте флориста (текущий статус: '{order.Status}')");
        }

        string address = order.DeliveryAddress;
        if (p.TryGetProperty("address", out var addrEl) && addrEl.ValueKind == JsonValueKind.String)
        {
            address = addrEl.GetString() ?? address;
        }
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new OperationException(ErrorCodes.ValidationError, "Не указан адрес доставки");
        }

        DateTime plannedAt = order.DeliveryDate;
        if (p.TryGetProperty("plannedAt", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            plannedAt = DateTime.Parse(dateEl.GetString()!);
        }

        var delivery = new Delivery
        {
            Id = _db.NextDeliveryId(),
            OrderId = order.Id,
            Address = address,
            PlannedAt = plannedAt,
            Status = "Planned"
        };
        _db.Deliveries.Add(delivery);

        order.Status = OrderStatus.DeliveryArranged;
        order.DeliveryAddress = address;
        InMemoryDataStore.AddEvent(order, "DeliveryArranged");

        // Флорист освобождается после оформления доставки
        if (order.FloristId is int fid)
        {
            var florist = _db.Florists.FirstOrDefault(f => f.Id == fid);
            if (florist is not null) florist.IsBusy = false;
        }

        return Response.Ok(new { delivery.Id, delivery.OrderId, delivery.Address, delivery.PlannedAt, delivery.Status },
            $"Доставка для заказа #{order.Id} запланирована на {plannedAt:dd.MM.yyyy HH:mm} по адресу: {address}");
    }

    // ===== Операции ЛР №3: синхронизация доступа к общему ресурсу ==========

    /// <summary>
    /// Задержка по умолчанию ВНУТРИ критической секции, мс. В ЛР №2 это было «окно
    /// гонки» между проверкой и изменением; в ЛР №3 задержка выполняется уже под
    /// блокировкой и наглядно показывает, что второй клиент обязан ждать (Требование 7).
    /// </summary>
    public const int DefaultCriticalDelayMs = 3000;

    /// <summary>
    /// ReserveBouquet — бронирование букета (вариант 14: общий ресурс — «доступный букет»).
    /// Последовательность «ПРОВЕРКА → изменение» выполняется как единая неделимая
    /// операция внутри критической секции (Требования 4, 6) механизмом <see cref="Sync"/>.
    /// </summary>
    public async Task<Response> ReserveBouquetAsync(JsonElement? payload, RequestContext? context = null, CancellationToken ct = default)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуется Id букета");

        int bouquetId = GetInt(p, "bouquetId");

        // Задержка внутри критической секции. Поле 'raceDelayMs' сохранено как псевдоним
        // для совместимости со сценарием ЛР №2.
        int criticalDelayMs = GetOptionalInt(p, "criticalDelayMs")
                              ?? GetOptionalInt(p, "raceDelayMs")
                              ?? DefaultCriticalDelayMs;
        if (criticalDelayMs < 0 || criticalDelayMs > RequestHandler.MaxDelayMs)
        {
            throw new OperationException(ErrorCodes.ValidationError,
                $"Поле 'criticalDelayMs' должно быть в диапазоне 0..{RequestHandler.MaxDelayMs} мс");
        }

        Bouquet bouquet = _db.Bouquets.FirstOrDefault(b => b.Id == bouquetId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден");

        return Sync switch
        {
            SyncMechanism.None => await ReserveWithoutSyncAsync(bouquet, bouquetId, criticalDelayMs, context, ct),
            SyncMechanism.SemaphoreSlim => await ReserveWithSemaphoreAsync(bouquet, bouquetId, criticalDelayMs, context, ct),
            _ => ReserveWithLock(bouquet, bouquetId, criticalDelayMs, context),
        };
    }

    /// <summary>
    /// Режим БЕЗ синхронизации (поведение ЛР №2): проверка и изменение ресурса НЕ атомарны.
    /// Оставлен для эксперимента «до исправления» и заполнения Таблицы 1.
    /// </summary>
    private async Task<Response> ReserveWithoutSyncAsync(Bouquet bouquet, int bouquetId, int criticalDelayMs,
        RequestContext? context, CancellationToken ct)
    {
        // Шаг 1: ПРОВЕРКА — чтение общего ресурса в локальную переменную (без блокировки)
        int available = bouquet.AvailableCount;
        Log(context, "READ", $"Букет #{bouquetId} — доступно {available} шт. (без синхронизации)");

        // Шаг 2: окно гонки — между проверкой и изменением проходит время,
        // за которое другой поток успевает изменить общий ресурс
        if (criticalDelayMs > 0)
        {
            await Task.Delay(criticalDelayMs, ct);
        }

        // Шаг 3: РЕШЕНИЕ принимается по УСТАРЕВШЕМУ значению, а запись выполняется
        // в текущее состояние: оба клиента читают «1» и оба уменьшают ресурс —
        // потерянное обновление, AvailableCount уходит в минус (Race Condition)
        if (available <= 0)
        {
            Log(context, "FAIL", $"Букет #{bouquetId} — отказ: свободных экземпляров не осталось");
            throw new OperationException(ErrorCodes.OutOfStock,
                $"Букет «{bouquet.Name}» недоступен для бронирования: свободных экземпляров не осталось");
        }

        bouquet.AvailableCount--;   // запись без синхронизации — возможна потеря обновления
        Log(context, "WRITE", $"Букет #{bouquetId} — установлено {bouquet.AvailableCount} шт. (без синхронизации)");
        Log(context, "SUCCESS", $"Букет #{bouquetId} — бронирование подтверждено (без синхронизации)");

        return Response.Ok(
            new { bouquetId, bouquet.Name, AvailableAfter = bouquet.AvailableCount },
            $"Букет «{bouquet.Name}» успешно забронирован. Доступно после бронирования: {bouquet.AvailableCount} шт.");
    }

    /// <summary>
    /// Режим lock (Требование 5): ПРОВЕРКА и ИЗМЕНЕНИЕ ресурса выполняются как единая
    /// неделимая операция. Пока один клиент находится внутри критической секции, второй
    /// войти в неё не может и обязан ждать освобождения блокировки (Требование 7).
    /// lock — синхронный примитив, поэтому задержка внутри секции выполнена через
    /// Thread.Sleep: поток, вошедший в lock, удерживается до выхода из секции.
    /// </summary>
    private Response ReserveWithLock(Bouquet bouquet, int bouquetId, int criticalDelayMs, RequestContext? context)
    {
        lock (_syncObject)   // ← вход в критическую секцию (взаимное исключение)
        {
            Log(context, "LOCK", $"Букет #{bouquetId} — вход в критическую секцию (lock)");
            try
            {
                // ПРОВЕРКА и ИЗМЕНЕНИЕ находятся в ОДНОЙ критической секции (Требование 6):
                // если вынести проверку наружу, Race Condition вернётся.
                Log(context, "READ", $"Букет #{bouquetId} — доступно {bouquet.AvailableCount} шт.");
                if (criticalDelayMs > 0)
                {
                    Thread.Sleep(criticalDelayMs);
                }

                return CheckAndUpdate(bouquet, bouquetId, context);
            }
            finally
            {
                Log(context, "UNLOCK", $"Букет #{bouquetId} — выход из критической секции (lock)");
            }
        }
    }

    /// <summary>
    /// Режим SemaphoreSlim (Требование 9): тот же участок защищён семафором на одно
    /// разрешение. В отличие от lock, WaitAsync НЕ блокирует поток — задача «паркуется»
    /// до освобождения семафора, а внутри критической секции допустим await.
    /// </summary>
    private async Task<Response> ReserveWithSemaphoreAsync(Bouquet bouquet, int bouquetId, int criticalDelayMs,
        RequestContext? context, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);   // ← вход в критическую секцию
        try
        {
            Log(context, "LOCK", $"Букет #{bouquetId} — вход в критическую секцию (SemaphoreSlim)");
            Log(context, "READ", $"Букет #{bouquetId} — доступно {bouquet.AvailableCount} шт.");
            if (criticalDelayMs > 0)
            {
                await Task.Delay(criticalDelayMs, ct);
            }

            return CheckAndUpdate(bouquet, bouquetId, context);
        }
        finally
        {
            Log(context, "UNLOCK", $"Букет #{bouquetId} — выход из критической секции (SemaphoreSlim)");
            _semaphore.Release();          // ← выход из критической секции
        }
    }

    /// <summary>
    /// Ядро критической секции: РЕШЕНИЕ по АКТУАЛЬНОМУ значению ресурса и его ИЗМЕНЕНИЕ.
    /// Вызывается ТОЛЬКО изнутри критической секции (под lock или SemaphoreSlim).
    /// </summary>
    private static Response CheckAndUpdate(Bouquet bouquet, int bouquetId, RequestContext? context)
    {
        if (bouquet.AvailableCount <= 0)
        {
            Log(context, "FAIL", $"Букет #{bouquetId} — отказ: свободных экземпляров не осталось");
            throw new OperationException(ErrorCodes.OutOfStock,
                $"Букет «{bouquet.Name}» недоступен для бронирования: свободных экземпляров не осталось");
        }

        bouquet.AvailableCount--;
        Log(context, "WRITE", $"Букет #{bouquetId} — установлено {bouquet.AvailableCount} шт.");
        Log(context, "SUCCESS", $"Букет #{bouquetId} — бронирование подтверждено");

        return Response.Ok(
            new { bouquetId, bouquet.Name, AvailableAfter = bouquet.AvailableCount },
            $"Букет «{bouquet.Name}» успешно забронирован. Доступно после бронирования: {bouquet.AvailableCount} шт.");
    }

    /// <summary>
    /// Запись события синхронизации в журнал сервера (Требование 8).
    /// В unit-тестах бизнес-логики контекст может отсутствовать — тогда журнал не ведётся.
    /// </summary>
    private static void Log(RequestContext? context, string phase, string detail)
        => context?.Journal?.Invoke(phase, detail);

    /// <summary>
    /// SetBouquetAvailability — установка количества доступных экземпляров букета (общий ресурс).
    /// Изменение ресурса также выполняется в критической секции: нельзя менять состояние
    /// одновременно с бронированием. Перед экспериментом количество ресурса = 1 (Требование 6).
    /// </summary>
    public async Task<Response> SetBouquetAvailabilityAsync(JsonElement? payload, RequestContext? context = null,
        CancellationToken ct = default)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуются bouquetId и count");

        int bouquetId = GetInt(p, "bouquetId");
        int count = GetInt(p, "count");
        if (count < 0)
        {
            throw new OperationException(ErrorCodes.ValidationError, "Количество ('count') не может быть отрицательным");
        }

        Bouquet bouquet = _db.Bouquets.FirstOrDefault(b => b.Id == bouquetId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден");

        // Запись в общий ресурс — под защитой выбранного механизма синхронизации.
        switch (Sync)
        {
            case SyncMechanism.SemaphoreSlim:
                await _semaphore.WaitAsync(ct);
                try { bouquet.AvailableCount = count; }
                finally { _semaphore.Release(); }
                break;

            case SyncMechanism.None:
                bouquet.AvailableCount = count;   // без синхронизации — только для эксперимента «до»
                break;

            default:
                lock (_syncObject) { bouquet.AvailableCount = count; }
                break;
        }

        Log(context, "WRITE", $"SetBouquetAvailability Букет #{bouquetId} — AvailableCount = {count}");
        return Response.Ok(new { bouquetId, bouquet.Name, AvailableCount = bouquet.AvailableCount },
            $"Количество доступных букетов «{bouquet.Name}» установлено: {count} шт.");
    }

    /// <summary>
    /// GetBouquetStatus — состояние общего ресурса (количество доступных букетов).
    /// Если передан bouquetId — возвращается один букет, иначе — все.
    /// </summary>
    public Response GetBouquetStatus(JsonElement? payload)
    {
        if (payload is JsonElement p && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("bouquetId", out _))
        {
            int bouquetId = GetInt(p, "bouquetId");
            Bouquet bouquet = _db.Bouquets.FirstOrDefault(b => b.Id == bouquetId)
                ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден");
            return Response.Ok(new { bouquetId, bouquet.Name, AvailableCount = bouquet.AvailableCount },
                $"Букет «{bouquet.Name}»: доступно {bouquet.AvailableCount} шт.");
        }

        var all = _db.Bouquets.Select(b => new { b.Id, b.Name, AvailableCount = b.AvailableCount }).ToList();
        return Response.Ok(new { bouquets = all }, "Состояние общего ресурса получено");
    }

    // ===== Операция ЛР №3, часть 2: синхронизация времени ==================

    /// <summary>
    /// GetServerTime — TIME_REQUEST из методички: сервер фиксирует собственное время
    /// (DateTime.UtcNow) и возвращает его клиенту (TIME_RESPONSE). Время передаётся
    /// в Unix-миллисекундах (устойчиво к настройкам локали) и строкой ISO-8601.
    ///
    /// Искусственная задержка <c>delayMs</c> обрабатывается RequestHandler ДО вызова
    /// этой операции, поэтому сдвигает момент фиксации времени — это используется
    /// в эксперименте о влиянии задержки на точность синхронизации (Требование 7).
    /// </summary>
    public Task<Response> GetServerTimeAsync(RequestContext? context = null)
    {
        DateTime serverTime = DateTime.UtcNow;   // замер времени сервера
        Log(context, "TIME", $"Время сервера: {serverTime:HH:mm:ss.fff} UTC");

        return Task.FromResult(Response.Ok(new
        {
            serverTimeUtc = serverTime.ToString("o"),
            serverTimeUnixMs = new DateTimeOffset(serverTime).ToUnixTimeMilliseconds()
        }, $"Время сервера (UTC): {serverTime:HH:mm:ss.fff}"));
    }

    // ----- Вспомогательные методы чтения параметров ----------------------

    private static int GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть целым числом");
    }

    /// <summary>Чтение опционального целого поля (null — если поля нет).</summary>
    private static int? GetOptionalInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть целым числом");
    }

    private static decimal GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out decimal s)) return s;
        throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть числом");
    }

    private static string GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        string? s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        if (string.IsNullOrWhiteSpace(s))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' не должно быть пустым");
        }
        return s;
    }

    private static DateTime GetDate(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        string? s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        if (string.IsNullOrWhiteSpace(s) || !DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime date))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть датой (например, 2026-09-30)");
        }
        return date;
    }
}

