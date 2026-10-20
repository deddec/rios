using System.Text.Json;
using FlowerShop.Server.Data;
using FlowerShop.Server.PubSub;
using FlowerShop.Server.Replication;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;
using FlowerShop.Shared.PubSub;

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
///
/// ЛР №5: при заданном <see cref="Replication"/> операции с БД выполняются через
/// активный экземпляр (Primary, либо Replica после Failover), а после успешного
/// изменения данные реплицируются в резервный экземпляр.
/// </summary>
public class BusinessLogic
{
    private readonly InMemoryDataStore _db;

    /// <summary>
    /// ЛР №4: репозиторий заказов в MSSQL. Если задан, операции предметной области
    /// выполняются через базу данных (архитектура Client → Server → Database);
    /// если null — работает старое поведение на памяти (ЛР №1–№3, unit-тесты).
    /// </summary>
    private readonly OrderRepository? _repository;

    /// <summary>Доступ к репозиторию БД (null — если БД не подключена).</summary>
    public OrderRepository? Repository => _repository;

    /// <summary>
    /// ЛР №5: менеджер репликации и отказоустойчивости (Primary + Replica).
    /// Если задан, операции с БД идут через активный экземпляр, а после записи
    /// выполняется репликация в резервный экземпляр. Имеет приоритет над
    /// <see cref="_repository"/> (одиночная БД ЛР №4).
    /// </summary>
    private readonly ReplicationManager? _replication;

    /// <summary>Менеджер репликации (null — если отказоустойчивость не включена).</summary>
    public ReplicationManager? Replication => _replication;

    /// <summary>
    /// ЛР №6: механизм Pub/Sub (Publisher, Broker, Subscriber). Если задан, операции
    /// предметной области формируют события (OrderCreated, BouquetPrepared,
    /// DeliveryArranged), которые Publisher передаёт Broker'у, а Broker — подписчикам.
    /// </summary>
    private readonly PubSubManager? _pubSub;

    /// <summary>Менеджер Pub/Sub (null — если ЛР №6 не подключена).</summary>
    public PubSubManager? PubSub => _pubSub;

    /// <summary>true — данные предметной области хранятся в БД (одиночной или реплицированной).</summary>
    private bool DbEnabled => _repository is not null || _replication is not null;

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

    public BusinessLogic(InMemoryDataStore? db = null, SyncMechanism sync = SyncMechanism.Lock,
        OrderRepository? repository = null, ReplicationManager? replication = null,
        PubSubManager? pubSub = null)
    {
        _db = db ?? new InMemoryDataStore();
        Sync = sync;
        _repository = repository;
        _replication = replication;
        _pubSub = pubSub;
    }

    /// <summary>
    /// ЛР №5: получить репозиторий АКТИВНОГО экземпляра для обслуживания запроса.
    /// При включённой репликации определяет состояние Primary, при отказе выполняет
    /// Failover на Replica и возвращает репозиторий доступного экземпляра. Если
    /// недоступны оба экземпляра — пробрасывает <see cref="OperationException"/>
    /// (Требование 5 ч.4). Без репликации возвращает одиночный репозиторий ЛР №4.
    /// </summary>
    private OrderRepository ActiveRepository(RequestContext? context)
    {
        if (_replication is not null)
        {
            return _replication.GetActive(context).Repository;
        }
        return _repository ?? throw new OperationException(ErrorCodes.DatabaseError,
            "База данных не подключена: операция доступна только при запуске сервера с MSSQL (ЛР №4/№5)");
    }

    /// <summary>
    /// ЛР №5: реплицировать изменения из активного экземпляра в резервный ПОСЛЕ
    /// успешной операции изменения. Без репликации — нет действия.
    /// </summary>
    private void ReplicateAfterChange(RequestContext? context) => _replication?.ReplicateAfterChange(context);

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
            case "CreateOrder": return Task.FromResult(CreateOrder(request.Payload, context));
            case "CustomizeBouquet": return Task.FromResult(CustomizeBouquet(request.Payload, context));
            case "AssignFlorist": return Task.FromResult(AssignFlorist(request.Payload));
            case "ArrangeDelivery": return Task.FromResult(ArrangeDelivery(request.Payload, context));
            case "ReserveBouquet": return ReserveBouquetAsync(request.Payload, context, ct);
            case "SetBouquetAvailability": return SetBouquetAvailabilityAsync(request.Payload, context, ct);
            case "GetBouquetStatus": return Task.FromResult(GetBouquetStatus(request.Payload, context));
            // ЛР №3, часть 2: TIME_REQUEST / TIME_RESPONSE (GetServerTime + псевдоним TimeRequest)
            case "GetServerTime":
            case "TimeRequest": return GetServerTimeAsync(context);
            // ЛР №4: операции через базу данных (MSSQL)
            case "PlaceOrderAndReserve": return PlaceOrderAndReserveAsync(request.Payload, context, ct);
            case "ListOrders": return Task.FromResult(ListOrders());
            // ЛР №5: репликация и отказоустойчивость
            case "GetInstanceState": return Task.FromResult(GetInstanceState());
            case "CheckConsistency": return Task.FromResult(CheckConsistencyOp());
            case "SetInstanceState": return Task.FromResult(SetInstanceState(request.Payload));
            case "Failover": return Task.FromResult(FailoverOp(context));
            case "Recover": return Task.FromResult(RecoverOp(context));
            case "Synchronize": return Task.FromResult(SynchronizeOp(context));
            // ЛР №6: Pub/Sub (Publisher, Broker, Subscriber)
            case "PublishEvent": return PublishEventAsync(request.Payload, context, ct);
            case "Subscribe": return Task.FromResult(SubscribeOp(request.Payload));
            case "Unsubscribe": return Task.FromResult(UnsubscribeOp(request.Payload));
            case "AddSubscriber": return Task.FromResult(AddSubscriberOp(request.Payload));
            case "SetSubscriberState": return Task.FromResult(SetSubscriberStateOp(request.Payload));
            case "SetBrokerState": return Task.FromResult(SetBrokerStateOp(request.Payload));
            case "GetPubSubStatus": return Task.FromResult(GetPubSubStatusOp());
            case "GetEventJournal": return Task.FromResult(GetEventJournalOp(request.Payload));
            case "GetEventStatistics": return Task.FromResult(GetEventStatisticsOp());
            case "RetryQueuedEvents": return RetryQueuedEventsAsync(ct);
            default:
                throw new OperationException(
                    ErrorCodes.UnknownOperation,
                    $"Неизвестная операция: '{request.Operation}'. Доступны: CreateOrder, CustomizeBouquet, " +
                    "AssignFlorist, ArrangeDelivery, ReserveBouquet, SetBouquetAvailability, GetBouquetStatus, " +
                    "GetServerTime, PlaceOrderAndReserve, ListOrders, GetInstanceState, CheckConsistency, " +
                    "SetInstanceState, Failover, Recover, Synchronize, PublishEvent, Subscribe, Unsubscribe, " +
                    "AddSubscriber, SetSubscriberState, SetBrokerState, GetPubSubStatus, GetEventJournal, " +
                    "GetEventStatistics, RetryQueuedEvents");
        }
    }

    /// <summary>Синхронный диспетчер (обёртка над <see cref="ExecuteAsync"/>).</summary>
    public Response Execute(Request request, RequestContext? context = null) =>
        ExecuteAsync(request, context).GetAwaiter().GetResult();
    // ----- CreateOrder -------------------------------------------------

    public Response CreateOrder(JsonElement? payload, RequestContext? context = null)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуются данные заказа");

        int customerId = GetInt(p, "customerId");
        int bouquetId = GetInt(p, "bouquetId");
        string address = GetString(p, "deliveryAddress");
        DateTime deliveryDate = GetDate(p, "deliveryDate");

        // ЛР №4/№5: при подключённой БД заказ сохраняется через АКТИВНЫЙ экземпляр
        // (Primary, либо Replica после Failover), затем изменение реплицируется.
        if (DbEnabled)
        {
            Response response = CreateOrderInDb(ActiveRepository(context), customerId, bouquetId, address, deliveryDate);
            ReplicateAfterChange(context);
            // ЛР №6: событие OrderCreated публикуется в Pub/Sub (Publisher → Broker → Subscribers)
            PublishDomainEvent(EventTypes.OrderCreated, ReadEntityId(response, "orderId"), context,
                ("bouquetId", bouquetId.ToString()), ("customerId", customerId.ToString()));
            return response;
        }

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

        // ЛР №6: событие OrderCreated публикуется в Pub/Sub (Publisher → Broker → Subscribers)
        PublishDomainEvent(EventTypes.OrderCreated, order.Id, context,
            ("bouquetId", bouquetId.ToString()), ("customerId", customerId.ToString()));

        return Response.Ok(new { order.Id, order.Status, Bouquet = bouquet.Name, Total = bouquet.TotalPrice },
            $"Заказ #{order.Id} создан для {customer.FullName}. Сумма: {bouquet.TotalPrice} руб.");
    }

    // ----- CustomizeBouquet ---------------------------------------------

    public Response CustomizeBouquet(JsonElement? payload, RequestContext? context = null)
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

        // ЛР №6: событие BouquetPrepared публикуется в Pub/Sub
        PublishDomainEvent(EventTypes.BouquetPrepared, order.Id, context, ("bouquetId", bouquet.Id.ToString()));

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

    public Response ArrangeDelivery(JsonElement? payload, RequestContext? context = null)
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

        // ЛР №6: событие DeliveryArranged публикуется в Pub/Sub
        PublishDomainEvent(EventTypes.DeliveryArranged, order.Id, context,
            ("address", address), ("plannedAt", plannedAt.ToString("yyyy-MM-dd HH:mm")));

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

        // ЛР №4: общий ресурс хранится в БД — критическая секция работает с SQL-данными.
        if (_repository is not null)
        {
            return await ReserveBouquetInDbAsync(bouquetId, criticalDelayMs, context, ct);
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

        // ЛР №4/№5: запись общего ресурса выполняется в активном экземпляре БД (под критической секцией).
        if (DbEnabled)
        {
            return await SetBouquetAvailabilityInDbAsync(bouquetId, count, context, ct);
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
    public Response GetBouquetStatus(JsonElement? payload, RequestContext? context = null)
    {
        // ЛР №4/№5: состояние общего ресурса читается из активного экземпляра БД.
        if (DbEnabled)
        {
            return GetBouquetStatusFromDb(payload, context);
        }

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

    // ===== Операции ЛР №4: работа через базу данных (MSSQL) ==================

    /// <summary>
    /// CreateOrderInDb — создание заказа в БД (операция изменения, Требование 5).
    /// Клиент обращается только к серверу; сервер — к БД через <see cref="OrderRepository"/>.
    /// Ограничения целостности (FK Customers/Bouquets) контролирует сама СУБД.
    /// </summary>
    /// <summary>
    /// CreateOrderInDb — создание заказа в БД активного экземпляра (операция изменения).
    /// Клиент обращается только к серверу; сервер — к активному экземпляру через
    /// <see cref="OrderRepository"/>. Ограничения целостности (FK) контролирует СУБД.
    /// </summary>
    private Response CreateOrderInDb(OrderRepository repo, int customerId, int bouquetId, string address, DateTime deliveryDate)
    {
        if (!repo.CustomerExists(customerId))
        {
            throw new OperationException(ErrorCodes.NotFound, $"Клиент с Id={customerId} не найден в БД");
        }
        if (!repo.BouquetExists(bouquetId))
        {
            throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден в БД");
        }

        int orderId = repo.CreateOrder(customerId, bouquetId, address, deliveryDate);
        if (orderId <= 0)
        {
            throw new OperationException(ErrorCodes.DatabaseError, "Не удалось получить Id созданного заказа");
        }

        return Response.Ok(new { orderId, customerId, bouquetId, status = OrderStatus.Created, deliveryDate, deliveryAddress = address },
            $"Заказ #{orderId} создан и сохранён в БД. Статус: {OrderStatus.Created}.");
    }

    /// <summary>
    /// ЛР №4, часть 2: бронирование букета — общий ресурс хранится в БД (Bouquets.AvailableCount).
    /// Критическая секция «проверка → резервирование» защищена выбранным механизмом
    /// (<see cref="Sync"/>): пока один клиент внутри секции, второй ждёт (lock/семафор).
    /// Внутри секции выполняется атомарный SQL-UPDATE ... WHERE AvailableCount > 0.
    /// </summary>
    private async Task<Response> ReserveBouquetInDbAsync(int bouquetId, int criticalDelayMs,
        RequestContext? context, CancellationToken ct)
    {
        // ЛР №5: резервирование выполняется в АКТИВНОМ экземпляре (Primary / Replica после Failover).
        var repo = ActiveRepository(context);

        Bouquet bouquet = repo.GetBouquet(bouquetId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден в БД");

        Response response = Sync switch
        {
            SyncMechanism.SemaphoreSlim => await ReserveInDbWithSemaphoreAsync(repo, bouquet, criticalDelayMs, context, ct),
            SyncMechanism.None => await ReserveInDbWithoutSync(repo, bouquet, criticalDelayMs, context, ct),
            _ => ReserveInDbWithLock(repo, bouquet, criticalDelayMs, context, ct),
        };

        // ЛР №5: после успешного изменения общего ресурса передать изменение в резервный экземпляр.
        ReplicateAfterChange(context);
        return response;
    }

    /// <summary>Режим lock: критическая секция «прочитать → решить → записать» в БД.</summary>
    private Response ReserveInDbWithLock(OrderRepository repo, Bouquet bouquet, int criticalDelayMs,
        RequestContext? context, CancellationToken ct)
    {
        int bouquetId = bouquet.Id;
        lock (_syncObject)
        {
            Log(context, "LOCK", $"Букет #{bouquetId} — вход в критическую секцию (lock, БД)");
            try
            {
                return CheckAndReserveInDb(repo, bouquet, criticalDelayMs, context, useThreadSleep: true, ct);
            }
            finally
            {
                Log(context, "UNLOCK", $"Букет #{bouquetId} — выход из критической секции (lock, БД)");
            }
        }
    }

    /// <summary>Режим SemaphoreSlim: та же секция, но ожидание асинхронное.</summary>
    private async Task<Response> ReserveInDbWithSemaphoreAsync(OrderRepository repo, Bouquet bouquet, int criticalDelayMs,
        RequestContext? context, CancellationToken ct)
    {
        int bouquetId = bouquet.Id;
        await _semaphore.WaitAsync(ct);
        try
        {
            Log(context, "LOCK", $"Букет #{bouquetId} — вход в критическую секцию (SemaphoreSlim, БД)");
            return CheckAndReserveInDb(repo, bouquet, criticalDelayMs, context, useThreadSleep: false, ct);
        }
        finally
        {
            Log(context, "UNLOCK", $"Букет #{bouquetId} — выход из критической секции (SemaphoreSlim, БД)");
            _semaphore.Release();
        }
    }

    /// <summary>Режим None: проверка и запись не атомарны — для сравнения «до» (ЛР №2).</summary>
    private async Task<Response> ReserveInDbWithoutSync(OrderRepository repo, Bouquet bouquet, int criticalDelayMs,
        RequestContext? context, CancellationToken ct)
    {
        int available = repo.GetAvailableCount(bouquet.Id);
        Log(context, "READ", $"Букет #{bouquet.Id} — доступно {available} шт. (без синхронизации, БД)");
        if (criticalDelayMs > 0)
        {
            await Task.Delay(criticalDelayMs, ct);   // окно гонки
        }
        if (available <= 0)
        {
            Log(context, "FAIL", $"Букет #{bouquet.Id} — отказ: свободных экземпляров не осталось");
            throw new OperationException(ErrorCodes.OutOfStock,
                $"Букет «{bouquet.Name}» недоступен для бронирования: свободных экземпляров не осталось");
        }

        bool reserved = repo.TryReserveOne(bouquet.Id);
        if (!reserved)
        {
            Log(context, "FAIL", $"Букет #{bouquet.Id} — отказ: конкурентный запрос успел забрать ресурс");
            throw new OperationException(ErrorCodes.OutOfStock,
                $"Букет «{bouquet.Name}» недоступен для бронирования: свободных экземпляров не осталось");
        }

        Log(context, "WRITE", $"Букет #{bouquet.Id} — резервирование выполнено (без синхронизации, БД)");
        Log(context, "SUCCESS", $"Букет #{bouquet.Id} — бронирование подтверждено (без синхронизации)");
        int after = repo.GetAvailableCount(bouquet.Id);
        return Response.Ok(new { bouquetId = bouquet.Id, bouquet.Name, AvailableAfter = after },
            $"Букет «{bouquet.Name}» забронирован (без синхронизации). Доступно после бронирования: {after} шт.");
    }

    /// <summary>
    /// Ядро критической секции для БД: ПРОВЕРКА доступности и ИЗМЕНЕНИЕ ресурса выполняются
    /// как единая неделимая операция (Требование 6): атомарный UPDATE с условием AvailableCount > 0.
    /// </summary>
    private Response CheckAndReserveInDb(OrderRepository repo, Bouquet bouquet, int criticalDelayMs,
        RequestContext? context, bool useThreadSleep, CancellationToken ct)
    {
        int bouquetId = bouquet.Id;

        Log(context, "READ", $"Букет #{bouquetId} — проверка ресурса в БД");
        if (criticalDelayMs > 0)
        {
            // lock удерживает поток, поэтому внутри lock — Thread.Sleep; семафор допускает ожидание.
            if (useThreadSleep) Thread.Sleep(criticalDelayMs);
            else ct.WaitHandle.WaitOne(criticalDelayMs);
        }

        bool reserved = repo.TryReserveOne(bouquetId);   // UPDATE ... WHERE AvailableCount > 0
        if (!reserved)
        {
            Log(context, "FAIL", $"Букет #{bouquetId} — отказ: свободных экземпляров не осталось");
            throw new OperationException(ErrorCodes.OutOfStock,
                $"Букет «{bouquet.Name}» недоступен для бронирования: свободных экземпляров не осталось");
        }

        int after = repo.GetAvailableCount(bouquetId);
        Log(context, "WRITE", $"Букет #{bouquetId} — установлено {after} шт. (БД)");
        Log(context, "SUCCESS", $"Букет #{bouquetId} — бронирование подтверждено");

        return Response.Ok(new { bouquetId, bouquet.Name, AvailableAfter = after },
            $"Букет «{bouquet.Name}» успешно забронирован. Доступно после бронирования: {after} шт.");
    }

    /// <summary>Установка количества ресурса в активном экземпляре БД под критической секцией.</summary>
    private async Task<Response> SetBouquetAvailabilityInDbAsync(int bouquetId, int count,
        RequestContext? context, CancellationToken ct)
    {
        var repo = ActiveRepository(context);
        Bouquet bouquet = repo.GetBouquet(bouquetId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден в БД");

        switch (Sync)
        {
            case SyncMechanism.SemaphoreSlim:
                await _semaphore.WaitAsync(ct);
                try { repo.SetAvailableCount(bouquetId, count); }
                finally { _semaphore.Release(); }
                break;

            case SyncMechanism.None:
                repo.SetAvailableCount(bouquetId, count);
                break;

            default:
                lock (_syncObject) { repo.SetAvailableCount(bouquetId, count); }
                break;
        }

        Log(context, "WRITE", $"SetBouquetAvailability Букет #{bouquetId} — AvailableCount = {count} (БД)");

        // ЛР №5: изменение общего ресурса передаётся в резервный экземпляр.
        ReplicateAfterChange(context);

        return Response.Ok(new { bouquetId, bouquet.Name, AvailableCount = count },
            $"Количество доступных букетов «{bouquet.Name}» установлено: {count} шт. (сохранено в БД)");
    }

    /// <summary>Чтение состояния общего ресурса из активного экземпляра БД.</summary>
    private Response GetBouquetStatusFromDb(JsonElement? payload, RequestContext? context = null)
    {
        var repo = ActiveRepository(context);

        if (payload is JsonElement p && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("bouquetId", out _))
        {
            int bouquetId = GetInt(p, "bouquetId");
            Bouquet bouquet = repo.GetBouquet(bouquetId)
                ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден в БД");
            return Response.Ok(new { bouquetId, bouquet.Name, AvailableCount = bouquet.AvailableCount },
                $"Букет «{bouquet.Name}» (БД): доступно {bouquet.AvailableCount} шт.");
        }

        var all = repo.ListBouquets().Select(b => new { b.Id, b.Name, AvailableCount = b.AvailableCount }).ToList();
        return Response.Ok(new { bouquets = all }, "Состояние общего ресурса получено из БД");
    }

    /// <summary>
    /// ЛР №4, часть 3: составная бизнес-операция «создать заказ + зарезервировать букет».
    /// Два связанных изменения в БД:
    ///   Operation 1 — INSERT в dbo.Orders;
    ///   Operation 2 — UPDATE dbo.Bouquets (уменьшение общего ресурса).
    /// Режимы:
    ///   useTransaction=true  — BEGIN → обе операции → COMMIT; при ошибке → ROLLBACK (изменения отменяются);
    ///   useTransaction=false — операции выполняются автокоммитом; ошибка между ними оставляет
    ///                          ЧАСТИЧНОЕ изменение в БД (для сравнения, Требование 2).
    /// simulatedError=true — искусственно вызванная ошибка между операциями (Требование 5).
    /// </summary>
    public async Task<Response> PlaceOrderAndReserveAsync(JsonElement? payload, RequestContext? context = null,
        CancellationToken ct = default)
    {
        // ЛР №5: составная операция выполняется в АКТИВНОГО экземпляра (Primary / Replica после Failover).
        var repo = ActiveRepository(context);
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуются customerId, bouquetId, deliveryAddress, deliveryDate");

        int customerId = GetInt(p, "customerId");
        int bouquetId = GetInt(p, "bouquetId");
        string address = GetString(p, "deliveryAddress");
        DateTime deliveryDate = GetDate(p, "deliveryDate");
        bool useTransaction = GetOptionalBool(p, "useTransaction") ?? true;
        bool simulatedError = GetOptionalBool(p, "simulatedError") ?? false;

        Log(context, "TX_BEGIN",
            $"PlaceOrderAndReserve: BEGIN (транзакция={useTransaction}, имитация ошибки={simulatedError})");

        try
        {
            PlaceOrderOutcome outcome = await Task.Run(() =>
                repo.PlaceOrderAndReserve(customerId, bouquetId, address, deliveryDate, useTransaction, simulatedError), ct);

            Log(context, useTransaction ? "COMMIT" : "AUTOCOMMIT",
                $"PlaceOrderAndReserve: обе операции выполнены, заказ #{outcome.OrderId}, " +
                $"ресурс {outcome.AvailableBefore} → {outcome.AvailableAfter}");

            // ЛР №5: после успешной составной операции передать изменения в резервный экземпляр.
            ReplicateAfterChange(context);

            string msg = useTransaction
                ? $"Заказ #{outcome.OrderId} создан и букет зарезервирован в ОДНОЙ транзакции (COMMIT). " +
                  $"Ресурс: {outcome.AvailableBefore} → {outcome.AvailableAfter} шт."
                : $"Заказ #{outcome.OrderId} создан и букет зарезервирован БЕЗ транзакции (автокоммит). " +
                  $"Ресурс: {outcome.AvailableBefore} → {outcome.AvailableAfter} шт.";

            return Response.Ok(new
            {
                outcome.OrderId,
                outcome.BouquetId,
                outcome.AvailableBefore,
                outcome.AvailableAfter,
                outcome.UseTransaction,
                outcome.SimulatedError
            }, msg);
        }
        catch (OperationException ex)
        {
            Log(context, useTransaction ? "ROLLBACK" : "PARTIAL",
                $"PlaceOrderAndReserve: {ex.Message} " +
                (useTransaction
                    ? "— выполнен ROLLBACK, изменения отменены"
                    : "— транзакции не было, возможны частичные изменения"));
            throw;
        }
    }

    /// <summary>
    /// ListOrders — операция чтения из активного экземпляра БД (Требование 5): возвращает все заказы.
    /// </summary>
    public Response ListOrders(RequestContext? context = null)
    {
        var repo = ActiveRepository(context);
        var orders = repo.ListOrders()
            .Select(o => new { o.Id, o.CustomerId, o.BouquetId, o.Status, o.DeliveryDate, o.DeliveryAddress, o.FloristId })
            .ToList();
        return Response.Ok(new { count = orders.Count, orders = orders },
            $"Заказов в БД: {orders.Count}");
    }

    /// <summary>Менеджер репликации обязателен для операций ЛР №5 — понятная ошибка, если он не подключён.</summary>
    private ReplicationManager RequireReplication() =>
        _replication ?? throw new OperationException(ErrorCodes.DatabaseError,
            "Репликация не включена: операции ЛР №5 доступны только при запуске сервера с Primary и Replica");

    // ===== Операции ЛР №5: репликация и отказоустойчивость ================

    /// <summary>
    /// GetInstanceState — состояние экземпляров Primary и Replica (Требование 5 ч.1).
    /// Возвращает состояние каждого экземпляра (AVAILABLE/UNAVAILABLE), активный
    /// экземпляр, имена баз и признак согласованности данных.
    /// </summary>
    public Response GetInstanceState()
    {
        var manager = RequireReplication();
        manager.RefreshStates();
        InstanceStateReport report = manager.GetStateReport();

        return Response.Ok(new
        {
            primaryState = report.PrimaryState,
            replicaState = report.ReplicaState,
            activeInstance = report.ActiveInstance,
            primaryDatabase = report.PrimaryDatabase,
            replicaDatabase = report.ReplicaDatabase,
            consistent = report.Consistent,
            differences = report.Differences
        },
            $"Primary={report.PrimaryState}, Replica={report.ReplicaState}, активный={report.ActiveInstance}, " +
            $"согласованы={(report.Consistent ? "да" : "нет")}");
    }

    /// <summary>CheckConsistency — проверка согласованности Primary и Replica (Требование 7 ч.1).</summary>
    public Response CheckConsistencyOp()
    {
        var manager = RequireReplication();
        ConsistencyReport report = manager.CheckConsistency();

        return Response.Ok(new
        {
            consistent = report.Consistent,
            differences = report.Differences
        },
            report.Consistent
                ? "Primary и Replica находятся в согласованном состоянии (данные совпадают)"
                : $"Обнаружено расхождение данных: {string.Join("; ", report.Differences)}");
    }

    /// <summary>
    /// SetInstanceState — моделирование отказа/восстановления экземпляра (Требование 2 ч.3).
    /// payload: { "instance": "primary"|"replica", "available": true|false }.
    /// </summary>
    public Response SetInstanceState(JsonElement? payload)
    {
        var manager = RequireReplication();
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуются поля 'instance' и 'available'");

        string instance = GetString(p, "instance").ToLowerInvariant();
        bool available = GetBool(p, "available");

        ReplicaRole role = instance switch
        {
            "primary" => ReplicaRole.Primary,
            "replica" => ReplicaRole.Replica,
            _ => throw new OperationException(ErrorCodes.ValidationError,
                $"Неизвестный экземпляр: '{instance}'. Допустимо: primary, replica")
        };

        manager.SetInstanceState(role, available);

        return Response.Ok(new { instance, state = available ? "AVAILABLE" : "UNAVAILABLE" },
            $"Экземпляр '{instance}' переведён в состояние {(available ? "AVAILABLE" : "UNAVAILABLE")} " +
            $"(смоделирован {(available ? "восстановление" : "отказ")})");
    }

    /// <summary>Failover — переключение на резервный экземпляр (Требование 3 ч.3).</summary>
    public Response FailoverOp(RequestContext? context = null)
    {
        var manager = RequireReplication();

        bool switched = manager.Failover(context);
        if (!switched)
        {
            if (manager.ActiveRole == ReplicaRole.Replica)
            {
                return Response.Ok(new { activeInstance = manager.ActiveName },
                    $"Failover: приложение уже работает через {manager.ActiveName}");
            }
            throw new OperationException(ErrorCodes.FailoverError,
                "Невозможно выполнить Failover: резервный экземпляр недоступен");
        }

        return Response.Ok(new { activeInstance = manager.ActiveName },
            $"Failover выполнен: запросы теперь обслуживаются через {manager.ActiveName}");
    }

    /// <summary>Recover — восстановление Primary и синхронизация (Требования 1–2 ч.4).</summary>
    public Response RecoverOp(RequestContext? context = null)
    {
        var manager = RequireReplication();

        bool ok = manager.Recover(context);
        ConsistencyReport report = manager.CheckConsistency();

        if (!ok)
        {
            throw new OperationException(ErrorCodes.ReplicationError,
                $"Не удалось завершить восстановление Primary: {string.Join("; ", report.Differences)}");
        }

        return Response.Ok(new
        {
            primaryState = manager.PrimaryState,
            replicaState = manager.ReplicaState,
            activeInstance = manager.ActiveName,
            consistent = report.Consistent,
            differences = report.Differences
        },
            report.Consistent
                ? $"Recovery завершён: {manager.Primary.Name} восстановлен и синхронизирован. Primary = Replica."
                : $"Recovery завершён, но остались расхождения: {string.Join("; ", report.Differences)}");
    }

    /// <summary>Synchronize — синхронизация резервного экземпляра с активным (Требование 2 ч.4).</summary>
    public Response SynchronizeOp(RequestContext? context = null)
    {
        var manager = RequireReplication();

        bool ok = manager.Synchronize(context);
        ConsistencyReport report = manager.CheckConsistency();

        return Response.Ok(new { synchronized = ok, consistent = report.Consistent, differences = report.Differences },
            ok && report.Consistent
                ? "Синхронизация выполнена: Primary = Replica"
                : $"Синхронизация завершена с проблемами: {string.Join("; ", report.Differences)}");
    }

    /// <summary>Чтение обязательного логического поля payload.</summary>
    private static bool GetBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)) return b;
        throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть логическим (true/false)");
    }

    /// <summary>Чтение опционального логического поля (null — если поля нет).</summary>
    private static bool? GetOptionalBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)) return b;
        throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть логическим (true/false)");
    }

    // ===== ЛР №6: Pub/Sub (Publisher, Broker, Subscriber) ===================

    /// <summary>Менеджер Pub/Sub или понятная ошибка, если ЛР №6 не подключена.</summary>
    private PubSubManager RequirePubSub() =>
        _pubSub ?? throw new OperationException(ErrorCodes.PublishError,
            "Механизм Pub/Sub не подключён: запустите сервер с компонентами ЛР №6 (Publisher, Broker, Subscriber)");

    /// <summary>
    /// ЛР №6: публикация события предметной области (Server → Publisher → Broker → Subscribers).
    /// Вызывается после успешного изменения данных. Ошибка публикации НЕ прерывает
    /// предметную операцию — она фиксируется в журнале (слабая связанность компонентов).
    /// </summary>
    private void PublishDomainEvent(string eventType, int entityId, RequestContext? context,
        params (string Key, string Value)[] data)
    {
        if (_pubSub is null) return;
        try
        {
            var dict = data.ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase);
            ShopEvent shopEvent = _pubSub.CreateEvent(eventType, entityId, context?.ClientId, dict);
            PublishResult result = _pubSub.PublishAsync(shopEvent).GetAwaiter().GetResult();
            Log(context, "PUBLISH",
                $"{eventType} #{entityId}: Broker={result.BrokerName}, доставлено {result.DeliveredCount}/{result.Deliveries.Count}" +
                (result.FailedCount > 0 ? $", недоставлено {result.FailedCount} (см. очередь Broker)" : ""));
        }
        catch (Exception ex)
        {
            Log(context, "PUBLISH FAIL", $"{eventType} #{entityId}: {ex.Message}");
        }
    }

    /// <summary>Прочитать целочисленный идентификатор из данных ответа (событие OrderCreated при работе в БД).</summary>
    private static int ReadEntityId(Response response, string property)
    {
        if (response.Data is JsonElement d && d.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int id))
        {
            return id;
        }
        return 0;
    }

    /// <summary>
    /// PublishEvent — публикация события вручную (Требования 6–7 ч.1).
    /// payload: { "eventType", "entityId"?, "clientId"?, "data"? }.
    /// </summary>
    private async Task<Response> PublishEventAsync(JsonElement? payload, RequestContext? context,
        CancellationToken ct)
    {
        PubSubManager pubSub = RequirePubSub();
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуется поле 'eventType'");

        string eventType = GetString(p, "eventType");
        int entityId = GetOptionalInt(p, "entityId") ?? 0;
        int? clientId = GetOptionalInt(p, "clientId") ?? context?.ClientId;

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (p.TryGetProperty("data", out JsonElement dataEl) && dataEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in dataEl.EnumerateObject())
            {
                data[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
            }
        }

        ShopEvent shopEvent = pubSub.CreateEvent(eventType, entityId, clientId, data); // валидация типа события
        PublishResult result = await pubSub.PublishAsync(shopEvent, ct);

        Log(context, "PUBLISH", $"{eventType} #{entityId}: Broker={result.BrokerName}, " +
                                $"доставлено {result.DeliveredCount}/{result.Deliveries.Count}");

        if (!result.Success)
        {
            return Response.Fail(result.ErrorCode ?? ErrorCodes.PublishError,
                result.Error ?? "Ошибка публикации события");
        }

        return Response.Ok(new
        {
            eventId = shopEvent.EventId,
            eventType = shopEvent.EventType,
            entityId = shopEvent.EntityId,
            broker = result.BrokerName,
            delivered = result.DeliveredCount,
            queued = result.FailedCount,
            deliveries = result.Deliveries.Select(d => new
            {
                subscriber = d.Subscriber,
                status = d.Status,
                attempts = d.Attempts,
                elapsedMs = d.ElapsedMs,
                error = d.Error
            })
        }, $"Событие {eventType} #{entityId} опубликовано: доставлено {result.DeliveredCount}, " +
           $"не доставлено {result.FailedCount} (Broker: {result.BrokerName})");
    }

    /// <summary>Subscribe — регистрация подписки Subscriber'а на тип события (Требование 5 ч.1).</summary>
    public Response SubscribeOp(JsonElement? payload)
    {
        PubSubManager pubSub = RequirePubSub();
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуются поля 'subscriber' и 'eventType'");

        string subscriber = GetString(p, "subscriber");
        string eventType = GetString(p, "eventType");
        IReadOnlyCollection<string> subscriptions = pubSub.Subscribe(subscriber, eventType);

        return Response.Ok(new { subscriber, eventType, subscriptions = subscriptions.OrderBy(x => x) },
            $"Подписка {subscriber} на событие {eventType} зарегистрирована");
    }

    /// <summary>Unsubscribe — отмена подписки Subscriber'а на тип события.</summary>
    public Response UnsubscribeOp(JsonElement? payload)
    {
        PubSubManager pubSub = RequirePubSub();
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуются поля 'subscriber' и 'eventType'");

        string subscriber = GetString(p, "subscriber");
        string eventType = GetString(p, "eventType");
        IReadOnlyCollection<string> subscriptions = pubSub.Unsubscribe(subscriber, eventType);

        return Response.Ok(new { subscriber, eventType, subscriptions = subscriptions.OrderBy(x => x) },
            $"Подписка {subscriber} на событие {eventType} отменена");
    }

    /// <summary>
    /// AddSubscriber — добавление НОВОГО Subscriber'а без изменения Publisher
    /// (Часть 4, Требование 1). payload: { "kind", "eventTypes"? }.
    /// kind: logger | florist | notification | analytics.
    /// </summary>
    public Response AddSubscriberOp(JsonElement? payload)
    {
        PubSubManager pubSub = RequirePubSub();
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуется поле 'kind' (тип нового Subscriber)");

        string kind = GetString(p, "kind");
        var eventTypes = new List<string>();
        if (p.TryGetProperty("eventTypes", out JsonElement typesEl) && typesEl.ValueKind == JsonValueKind.Array)
        {
            eventTypes.AddRange(typesEl.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? ""));
        }

        ISubscriber subscriber = pubSub.AddSubscriber(kind, eventTypes);

        return Response.Ok(new
        {
            subscriber = subscriber.Name,
            kind,
            subscriptions = subscriber.Subscriptions.OrderBy(x => x),
            publisherUnchanged = true
        }, $"Новый Subscriber '{subscriber.Name}' зарегистрирован и подписан: " +
           $"[{string.Join(", ", subscriber.Subscriptions)}]. Publisher не изменялся.");
    }

    /// <summary>
    /// SetSubscriberState — моделирование отказа/восстановления Subscriber
    /// (Часть 3, Требование 2). payload: { "subscriber", "available" }.
    /// </summary>
    public Response SetSubscriberStateOp(JsonElement? payload)
    {
        PubSubManager pubSub = RequirePubSub();
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуются поля 'subscriber' и 'available'");

        string subscriber = GetString(p, "subscriber");
        bool available = GetBool(p, "available");
        ISubscriber sub = pubSub.SetSubscriberState(subscriber, available);

        return Response.Ok(new { subscriber = sub.Name, state = available ? "AVAILABLE" : "UNAVAILABLE" },
            $"Subscriber '{sub.Name}' переведён в состояние {(available ? "AVAILABLE" : "UNAVAILABLE")} " +
            $"(смоделирован {(available ? "восстановление" : "отказ")})");
    }

    /// <summary>
    /// SetBrokerState — моделирование отказа/восстановления Broker (Часть 5, Требование 1).
    /// payload: { "broker", "available" }.
    /// </summary>
    public Response SetBrokerStateOp(JsonElement? payload)
    {
        PubSubManager pubSub = RequirePubSub();
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError,
            "Пустой запрос: требуются поля 'broker' и 'available'");

        string broker = GetString(p, "broker");
        bool available = GetBool(p, "available");
        pubSub.SetBrokerState(broker, available);

        PubSubStatus status = pubSub.GetStatus();
        return Response.Ok(new { broker, state = available ? "AVAILABLE" : "UNAVAILABLE", activeBroker = status.ActiveBroker },
            $"Broker '{broker}' переведён в состояние {(available ? "AVAILABLE" : "UNAVAILABLE")}; " +
           $"активный Broker: {status.ActiveBroker}");
    }

    /// <summary>GetPubSubStatus — состояние Broker'ов, Subscriber'ов и очередей.</summary>
    public Response GetPubSubStatusOp()
    {
        PubSubManager pubSub = RequirePubSub();
        PubSubStatus status = pubSub.GetStatus();

        return Response.Ok(status,
            $"Активный Broker: {status.ActiveBroker}; Broker'ов: {status.BrokerCount}, " +
            $"Subscriber'ов: {status.SubscriberCount}, событий в очередях: {status.QueuedEvents}");
    }

    /// <summary>
    /// GetEventJournal — журнал публикации и доставки событий (Требования 11–12 ч.2).
    /// payload: { "limit"? } — вернуть только последние N записей.
    /// </summary>
    public Response GetEventJournalOp(JsonElement? payload)
    {
        PubSubManager pubSub = RequirePubSub();
        int? limit = payload is JsonElement p ? GetOptionalInt(p, "limit") : null;
        IReadOnlyList<PubSubLogEntry> entries = pubSub.GetJournal(limit);

        return Response.Ok(new { count = entries.Count, entries },
            $"Журнал Pub/Sub: записей {entries.Count}");
    }

    /// <summary>GetEventStatistics — статистика по событиям (Часть 5, Требование 6).</summary>
    public Response GetEventStatisticsOp()
    {
        PubSubManager pubSub = RequirePubSub();
        EventStatistics stats = pubSub.GetStatistics();

        return Response.Ok(stats,
            $"Опубликовано событий: {stats.Published}, ошибок публикации: {stats.PublishFailures}, " +
            $"в очередях: {stats.Queued}");
    }

    /// <summary>RetryQueuedEvents — повторная доставка событий из очередей Broker'ов (Часть 5, Требование 3).</summary>
    private async Task<Response> RetryQueuedEventsAsync(CancellationToken ct)
    {
        PubSubManager pubSub = RequirePubSub();
        IReadOnlyList<DeliveryResult> results = await pubSub.RetryQueuedEventsAsync(ct);

        int success = results.Count(r => r.Success);
        return Response.Ok(new
        {
            retried = results.Count,
            delivered = success,
            stillQueued = results.Count - success,
            deliveries = results.Select(r => new
            {
                subscriber = r.Subscriber,
                status = r.Status,
                attempts = r.Attempts,
                error = r.Error
            })
        }, results.Count == 0
            ? "Очереди Broker'ов пусты — повторная доставка не требуется"
            : $"Повторная доставка: {results.Count}, успешно: {success}, осталось в очереди: {results.Count - success}");
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

