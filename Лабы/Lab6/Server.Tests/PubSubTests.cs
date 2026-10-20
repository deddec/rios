using System.Text.Json;
using FlowerShop.Server;
using FlowerShop.Server.PubSub;
using FlowerShop.Shared.PubSub;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// ЛР №6: Unit-тесты модели Pub/Sub — создание события, публикация через Broker,
/// регистрация подписок, доставка (в том числе нескольким Subscriber'ам и только
/// подписанным), обработка отказа Subscriber, очередь и повторная доставка,
/// добавление нового Subscriber, работа с несколькими Broker и обработка ошибок.
/// Для тестов задержки Subscriber'ов равны нулю — проверяется логика, а не тайминги.
/// </summary>
public class PubSubTests
{
    // ----- Вспомогательные построители ------------------------------------

    private static LoggerSubscriber NewLogger() => new(TimeSpan.Zero);
    private static FloristServiceSubscriber NewFlorist() => new(TimeSpan.Zero);
    private static NotificationServiceSubscriber NewNotification() => new(TimeSpan.Zero);
    private static AnalyticsServiceSubscriber NewAnalytics() => new(TimeSpan.Zero);

    /// <summary>Менеджер с двумя Broker и тремя подписчиками, подписанными на все типы событий.</summary>
    private static (PubSubManager Manager, LoggerSubscriber Logger, FloristServiceSubscriber Florist,
        NotificationServiceSubscriber Notification) BuildManager()
    {
        var manager = new PubSubManager();
        var logger = (LoggerSubscriber)manager.RegisterSubscriber(NewLogger());
        var florist = (FloristServiceSubscriber)manager.RegisterSubscriber(NewFlorist());
        var notification = (NotificationServiceSubscriber)manager.RegisterSubscriber(NewNotification());

        foreach (string type in EventTypes.All)
        {
            manager.Subscribe(logger.Name, type);
            manager.Subscribe(florist.Name, type);
            manager.Subscribe(notification.Name, type);
        }
        return (manager, logger, florist, notification);
    }

    private static Request Req(string operation, object? payload = null) => new()
    {
        Operation = operation,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload)
    };

    private static Response Handle(BusinessLogic logic, Request request) =>
        new RequestHandler(logic).Handle(JsonSerializer.Serialize(request, Json.Options));

    // ----- Требование 2 ч.1: модель события --------------------------------

    [Fact]
    public void CreateEvent_ContainsRequiredFields()
    {
        var manager = new PubSubManager();

        ShopEvent shopEvent = manager.CreateEvent(EventTypes.OrderCreated, 125, clientId: 7,
            new Dictionary<string, string> { ["bouquetId"] = "2" });

        Assert.Equal(EventTypes.OrderCreated, shopEvent.EventType);
        Assert.Equal(125, shopEvent.EntityId);
        Assert.Equal(7, shopEvent.ClientId);
        Assert.NotEmpty(shopEvent.EventId);
        Assert.Equal(DateTime.Now.Year, shopEvent.Timestamp.Year);
        Assert.Equal("2", shopEvent.Data["bouquetId"]);
    }

    [Fact]
    public void CreateEvent_UnknownType_ThrowsValidationError()
    {
        var manager = new PubSubManager();

        var ex = Assert.Throws<OperationException>(() => manager.CreateEvent("UnknownEvent", 1));
        Assert.Equal(ErrorCodes.UnknownEventType, ex.ErrorCode);
    }

    // ----- Требования 6–7 ч.1: базовая доставка ----------------------------

    [Fact]
    public async Task Publish_DeliversEventToSubscribedSubscriber()
    {
        (PubSubManager manager, LoggerSubscriber logger, _, _) = BuildManager();
        ShopEvent shopEvent = manager.CreateEvent(EventTypes.OrderCreated, 125);

        PublishResult result = await manager.PublishAsync(shopEvent);

        Assert.True(result.Success);
        Assert.Equal("Broker-1", result.BrokerName);
        Assert.Contains(logger.Received, e => e.EventId == shopEvent.EventId);
    }

    [Fact]
    public async Task Publish_DoesNotDeliverToUnsubscribedSubscriber()
    {
        var manager = new PubSubManager();
        var logger = (LoggerSubscriber)manager.RegisterSubscriber(NewLogger());
        var notification = (NotificationServiceSubscriber)manager.RegisterSubscriber(NewNotification());
        manager.Subscribe(logger.Name, EventTypes.OrderCreated); // NotificationService не подписан

        await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 1));

        Assert.Single(logger.Received);
        Assert.Empty(notification.Received);
        Assert.False(notification.IsSubscribedTo(EventTypes.OrderCreated));
    }

    [Fact]
    public async Task Publish_OneEvent_DeliveredToMultipleSubscribers()
    {
        (PubSubManager manager, LoggerSubscriber logger, FloristServiceSubscriber florist,
            NotificationServiceSubscriber notification) = BuildManager();

        PublishResult result = await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 55));

        Assert.Equal(3, result.DeliveredCount);
        Assert.All(result.Deliveries, d => Assert.True(d.Success));
        Assert.Single(logger.Received);
        Assert.Single(florist.Received);
        Assert.Single(notification.Received);
    }

    // ----- Требование 4 ч.2: несколько типов событий -----------------------

    [Fact]
    public async Task MultipleEventTypes_SubscriberReceivesOnlySubscribedTypes()
    {
        var manager = new PubSubManager();
        var logger = (LoggerSubscriber)manager.RegisterSubscriber(NewLogger());
        manager.Subscribe(logger.Name, EventTypes.OrderCreated);
        manager.Subscribe(logger.Name, EventTypes.DeliveryArranged); // BouquetPrepared не подписан

        await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 1));
        await manager.PublishAsync(manager.CreateEvent(EventTypes.BouquetPrepared, 1));
        await manager.PublishAsync(manager.CreateEvent(EventTypes.DeliveryArranged, 1));

        Assert.Equal(2, logger.Received.Count);
        Assert.DoesNotContain(logger.Received, e => e.EventType == EventTypes.BouquetPrepared);
        Assert.Contains(logger.Received, e => e.EventType == EventTypes.DeliveryArranged);
    }

    [Fact]
    public async Task Publish_WithoutSubscribers_SucceedsWithoutDelivery()
    {
        var manager = new PubSubManager(); // подписчиков нет

        PublishResult result = await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 9));

        Assert.True(result.Success);
        Assert.Empty(result.Deliveries);
        Assert.Contains(manager.Journal, e => e.Phase == "NO SUBSCRIBERS");
    }

    // ----- Часть 3: отказ Subscriber и продолжение работы остальных ---------

    [Fact]
    public async Task SubscriberUnavailable_OtherSubscribersContinue_BrokerStaysAvailable()
    {
        (PubSubManager manager, LoggerSubscriber logger, FloristServiceSubscriber florist,
            NotificationServiceSubscriber notification) = BuildManager();
        manager.SetSubscriberState(notification.Name, available: false); // смоделированный отказ

        PublishResult result = await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 7));

        Assert.True(result.Success); // Broker не завершается, публикация принята
        DeliveryResult failed = Assert.Single(result.Deliveries, d => !d.Success);
        Assert.Equal(notification.Name, failed.Subscriber);
        Assert.Equal("UNAVAILABLE", failed.Status);
        Assert.True(result.DeliveredCount >= 2); // остальные продолжили работу
        Assert.Single(logger.Received);
        Assert.Single(florist.Received);
        Assert.Empty(notification.Received);
        Assert.True(manager.ActiveBroker!.IsAvailable);
        Assert.Contains(manager.Journal, e => e.Phase == "QUEUED" && e.Subscriber == notification.Name);
    }

    [Fact]
    public async Task FailedDelivery_Queued_AndRetriedSuccessfullyAfterRecovery()
    {
        (PubSubManager manager, LoggerSubscriber logger, FloristServiceSubscriber florist,
            NotificationServiceSubscriber notification) = BuildManager();
        manager.SetSubscriberState(notification.Name, available: false);

        ShopEvent shopEvent = manager.CreateEvent(EventTypes.DeliveryArranged, 42);
        await manager.PublishAsync(shopEvent);
        Assert.Equal(1, manager.Brokers[0].QueueCount); // событие не потеряно

        manager.SetSubscriberState(notification.Name, available: true);
        IReadOnlyList<DeliveryResult> results = await manager.RetryQueuedEventsAsync();

        Assert.Single(results); // одно событие в очереди
        Assert.True(results[0].Success);
        Assert.Equal(0, manager.Brokers[0].QueueCount);
        Assert.Contains(notification.Received, e => e.EventId == shopEvent.EventId);
        Assert.Single(logger.Received); // остальные не получили событие повторно
        Assert.Single(florist.Received);
    }

    private sealed class FlakySubscriber : SubscriberBase
    {
        private int _calls;

        public FlakySubscriber() : base("FlakyService", TimeSpan.Zero) { }

        protected override Task OnEventAsync(ShopEvent shopEvent, CancellationToken ct)
        {
            // Первые две попытки — искусственная ошибка, третья — успех
            if (Interlocked.Increment(ref _calls) <= 2)
            {
                throw new InvalidOperationException("Временная ошибка обработки события");
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Delivery_RetriesUntilThirdAttemptSucceeds()
    {
        var manager = new PubSubManager();
        var logger = (LoggerSubscriber)manager.RegisterSubscriber(NewLogger());
        var flaky = (FlakySubscriber)manager.RegisterSubscriber(new FlakySubscriber());
        manager.Subscribe(logger.Name, EventTypes.OrderCreated);
        manager.Subscribe(flaky.Name, EventTypes.OrderCreated);
        manager.Brokers[0].RetryDelay = TimeSpan.Zero;

        PublishResult result = await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 3));

        DeliveryResult flakyResult = Assert.Single(result.Deliveries, d => d.Subscriber == flaky.Name);
        Assert.True(flakyResult.Success);
        Assert.Equal(3, flakyResult.Attempts); // Attempt 1 > FAIL, Attempt 2 > FAIL, Attempt 3 > SUCCESS
        Assert.Empty(manager.Brokers[0].Queued);
    }

    // ----- Часть 4: новый Subscriber без изменения Publisher ----------------

    [Fact]
    public async Task AddNewSubscriber_ReceivesExistingEvents_PublisherUnchanged()
    {
        (PubSubManager manager, LoggerSubscriber logger, _, _) = BuildManager();
        object publisherBefore = manager.Publisher; // Publisher не изменяется

        ISubscriber analytics = manager.AddSubscriber("analytics",
            new[] { EventTypes.OrderCreated, EventTypes.BouquetPrepared });

        Assert.IsType<AnalyticsServiceSubscriber>(analytics);
        Assert.Same(publisherBefore, manager.Publisher);

        PublishResult result = await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 300));

        Assert.Contains(result.Deliveries, d => d.Subscriber == analytics.Name && d.Success);
        var analyticsService = (AnalyticsServiceSubscriber)analytics;
        Assert.Contains(analyticsService.Received, e => e.EntityId == 300);
        Assert.Single(logger.Received); // прежние подписчики работают как раньше
    }

    [Fact]
    public void AddSubscriber_DuplicateName_Throws()
    {
        (PubSubManager manager, _, _, _) = BuildManager();

        var ex = Assert.Throws<OperationException>(() => manager.AddSubscriber("logger"));
        Assert.Contains("уже зарегистрирован", ex.Message);
    }

    // ----- Часть 5: несколько Broker ----------------------------------------

    [Fact]
    public async Task BrokerUnavailable_PublisherFailsOverToSecondBroker()
    {
        (PubSubManager manager, LoggerSubscriber logger, _, _) = BuildManager();
        manager.SetBrokerState("Broker-1", available: false); // отказ активного Broker

        PublishResult result = await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 11));

        Assert.True(result.Success);
        Assert.Equal("Broker-2", result.BrokerName); // публикация через резервный Broker
        Assert.Single(logger.Received);
        Assert.Contains(manager.Journal, e => e.Phase == "BROKER SKIP" && e.Detail != null && e.Detail.Contains("Broker-1"));
    }

    [Fact]
    public async Task AllBrokersUnavailable_PublishReturnsErrorWithoutThrowing()
    {
        (PubSubManager manager, _, _, _) = BuildManager();
        manager.SetBrokerState("Broker-1", available: false);
        manager.SetBrokerState("Broker-2", available: false);

        PublishResult result = await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 12));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.BrokerUnavailable, result.ErrorCode);
        Assert.NotNull(result.Error);
        Assert.Null(manager.ActiveBroker);
    }

    [Fact]
    public void SetBrokerState_UnknownBroker_ThrowsWithClearMessage()
    {
        (PubSubManager manager, _, _, _) = BuildManager();

        var ex = Assert.Throws<OperationException>(() => manager.SetBrokerState("Broker-99", false));
        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
        Assert.Contains("Broker-1", ex.Message);
    }

    // ----- Журнал и статистика (Требования 11–12 ч.2, 6 ч.5) ----------------

    [Fact]
    public async Task Journal_ContainsPublishAndDeliveryRecords()
    {
        (PubSubManager manager, _, _, _) = BuildManager();

        await manager.PublishAsync(manager.CreateEvent(EventTypes.BouquetPrepared, 5, clientId: 3));

        Assert.Contains(manager.Journal, e => e.Phase == "PUBLISH" && e.Component == "Publisher");
        Assert.Contains(manager.Journal, e => e.Phase == "EVENT RECEIVED" && e.Component == "Broker-1");
        Assert.Contains(manager.Journal, e => e.Phase == "DELIVER" && e.Subscriber is not null);
        Assert.Contains(manager.Journal, e => e.Phase == "SUCCESS" && e.Subscriber == "Logger");
        Assert.Contains(manager.Journal, e => e.ClientId == 3 && e.EventType == EventTypes.BouquetPrepared);
    }

    [Fact]
    public async Task Statistics_CountsPublishedAndDeliveredEvents()
    {
        (PubSubManager manager, _, _, NotificationServiceSubscriber notification) = BuildManager();
        manager.SetSubscriberState(notification.Name, available: false);

        await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 1));
        await manager.PublishAsync(manager.CreateEvent(EventTypes.OrderCreated, 2));
        manager.SetSubscriberState(notification.Name, available: true);
        await manager.RetryQueuedEventsAsync();

        EventStatistics stats = manager.GetStatistics();
        Assert.Equal(2, stats.Published);
        Assert.Equal(2, stats.PublishedByType[EventTypes.OrderCreated]);
        Assert.Equal(2, stats.DeliveredBySubscriber["Logger"]);
        Assert.Equal(2, stats.DeliveredBySubscriber[notification.Name]);
        Assert.Equal(2, stats.FailedBySubscriber[notification.Name]); // две недоставки в очереди
        Assert.Equal(0, stats.Queued);
    }

    // ----- Интеграция: операции сервера и события предметной области ---------

    private static BusinessLogic LogicWithPubSub(out PubSubManager manager)
    {
        var m = new PubSubManager();
        var logger = (LoggerSubscriber)m.RegisterSubscriber(NewLogger());
        var florist = (FloristServiceSubscriber)m.RegisterSubscriber(NewFlorist());
        var notification = (NotificationServiceSubscriber)m.RegisterSubscriber(NewNotification());
        foreach (string type in EventTypes.All)
        {
            m.Subscribe(logger.Name, type);
            m.Subscribe(florist.Name, type);
            m.Subscribe(notification.Name, type);
        }
        manager = m;
        return new BusinessLogic(pubSub: m);
    }

    [Fact]
    public void CreateOrder_PublishesOrderCreatedEventToSubscribers()
    {
        BusinessLogic logic = LogicWithPubSub(out PubSubManager manager);

        Response response = Handle(logic, Req("CreateOrder", new
        {
            customerId = 1,
            bouquetId = 1,
            deliveryAddress = "ул. Ленина, 10",
            deliveryDate = "2026-12-31"
        }));

        Assert.True(response.Success);
        var logger = Assert.IsType<LoggerSubscriber>(manager.RequireSubscriber("Logger"));
        Assert.Single(logger.Received);
        Assert.Equal(EventTypes.OrderCreated, logger.Received[0].EventType);
        Assert.Contains(manager.Journal, e => e.Phase == "PUBLISH" && e.EventType == EventTypes.OrderCreated);
        Assert.Contains(manager.Journal, e => e.Phase == "SUCCESS" && e.Subscriber == "NotificationService");
    }

    [Fact]
    public void PublishEvent_Operation_ReturnsDeliveryResults()
    {
        BusinessLogic logic = LogicWithPubSub(out _);

        Response response = Handle(logic, Req("PublishEvent", new { eventType = "BouquetPrepared", entityId = 42 }));

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.True(response.Data!.Value.GetProperty("delivered").GetInt32() >= 3);
        Assert.Equal("Broker-1", response.Data!.Value.GetProperty("broker").GetString());
    }

    [Fact]
    public void PublishEvent_UnknownEventType_ReturnsError()
    {
        BusinessLogic logic = LogicWithPubSub(out _);

        Response response = Handle(logic, Req("PublishEvent", new { eventType = "HackedEvent", entityId = 1 }));

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.UnknownEventType, response.ErrorCode);
    }

    [Fact]
    public void Subscribe_UnknownSubscriber_ReturnsSubscriberNotRegistered()
    {
        BusinessLogic logic = LogicWithPubSub(out _);

        Response response = Handle(logic, Req("Subscribe", new { subscriber = "Ghost", eventType = "OrderCreated" }));

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.SubscriberNotRegistered, response.ErrorCode);
    }

    [Fact]
    public void PubSubOperations_NotConfigured_ReturnsClearError()
    {
        var logic = new BusinessLogic(); // Pub/Sub не подключён (как в ЛР №1–ЛР №5)

        Response response = Handle(logic, Req("GetPubSubStatus"));

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.PublishError, response.ErrorCode);
        Assert.Contains("Pub/Sub", response.Message);
    }

    [Fact]
    public void AddSubscriber_Operation_RegistersNewSubscriberWithoutTouchingPublisher()
    {
        BusinessLogic logic = LogicWithPubSub(out PubSubManager manager);
        object publisher = manager.Publisher;

        Response added = Handle(logic, Req("AddSubscriber",
            new { kind = "analytics", eventTypes = new[] { "OrderCreated", "BouquetPrepared", "DeliveryArranged" } }));
        Assert.True(added.Success);

        Response published = Handle(logic, Req("PublishEvent", new { eventType = "OrderCreated", entityId = 777 }));
        Assert.True(published.Success);

        Assert.Same(publisher, manager.Publisher);
        var analytics = (AnalyticsServiceSubscriber)manager.RequireSubscriber("AnalyticsService");
        Assert.Contains(analytics.Received, e => e.EntityId == 777);
    }
}