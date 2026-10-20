using System.Collections.Concurrent;
using FlowerShop.Shared.PubSub;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: менеджер модели Pub/Sub — владеет Publisher, несколькими Broker'ами и
/// Subscriber'ами, ведёт журнал публикации/доставки и статистику.
/// Схема: Server → Publisher → Broker → Subscribers. Реализация средствами C#.
/// </summary>
public sealed class PubSubManager
{
    private readonly List<EventBroker> _brokers = new();
    private readonly Dictionary<string, ISubscriber> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<PubSubLogEntry> _journal = new();
    private readonly Publisher _publisher;

    /// <summary>
    /// Создать менеджер с Broker'ами по умолчанию (Broker-1, Broker-2 — несколько
    /// Broker, Требование 1 ч.5) и указанными подписчиками (без подписок).
    /// </summary>
    public PubSubManager(params ISubscriber[] subscribers)
        : this(new[] { "Broker-1", "Broker-2" }, subscribers)
    {
    }

    /// <summary>Создать менеджер с заданными именами Broker'ов и подписчиками.</summary>
    public PubSubManager(IEnumerable<string> brokerNames, IEnumerable<ISubscriber>? subscribers = null)
    {
        foreach (string name in brokerNames)
        {
            _brokers.Add(new EventBroker(name) { OnLog = Enqueue });
        }
        if (_brokers.Count == 0)
        {
            throw new ArgumentException("Необходимо хотя бы один Broker", nameof(brokerNames));
        }

        _publisher = new Publisher(_brokers) { OnLog = Enqueue };

        foreach (ISubscriber subscriber in subscribers ?? Enumerable.Empty<ISubscriber>())
        {
            RegisterSubscriber(subscriber);
        }
    }

    /// <summary>Publisher, публикующий события (не знает состава подписчиков).</summary>
    public Publisher Publisher => _publisher;

    /// <summary>Все Broker'ы.</summary>
    public IReadOnlyList<EventBroker> Brokers => _brokers;

    /// <summary>Все зарегистрированные подписчики.</summary>
    public IReadOnlyList<ISubscriber> Subscribers => _subscribers.Values.ToArray();

    /// <summary>Активный Broker — первый доступный (при отказе — Failover на следующий).</summary>
    public EventBroker? ActiveBroker => _brokers.FirstOrDefault(b => b.IsAvailable);

    /// <summary>Журнал публикации и доставки событий (Требования 11–12 ч.2).</summary>
    public IReadOnlyList<PubSubLogEntry> Journal => _journal.ToArray();

    /// <summary>Внешний вывод журнала (консоль сервера); запись всегда попадает в <see cref="Journal"/>.</summary>
    public Action<PubSubLogEntry>? OnLog { get; set; }

    private void Enqueue(PubSubLogEntry entry)
    {
        _journal.Enqueue(entry);
        OnLog?.Invoke(entry);
    }

    // ===== Subscribers =====================================================

    /// <summary>
    /// Регистрация готового Subscriber'а: имя запоминается, ранее заданные подписки
    /// регистрируются во всех Broker'ах (Требование 5 ч.1).
    /// </summary>
    public ISubscriber RegisterSubscriber(ISubscriber subscriber)
    {
        if (_subscribers.ContainsKey(subscriber.Name))
        {
            throw new OperationException(ErrorCodes.ValidationError,
                $"Subscriber '{subscriber.Name}' уже зарегистрирован");
        }
        _subscribers[subscriber.Name] = subscriber;

        foreach (string eventType in subscriber.Subscriptions)
        {
            foreach (EventBroker broker in _brokers) broker.Subscribe(subscriber, eventType);
        }
        return subscriber;
    }

    /// <summary>Найти Subscriber'а или получить понятную ошибку (обработка ошибок, ч.4).</summary>
    public ISubscriber RequireSubscriber(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new OperationException(ErrorCodes.ValidationError, "Не указано имя Subscriber");
        }
        return _subscribers.TryGetValue(name, out ISubscriber? subscriber)
            ? subscriber
            : throw new OperationException(ErrorCodes.SubscriberNotRegistered,
                $"Subscriber '{name}' не зарегистрирован. Зарегистрированы: {string.Join(", ", _subscribers.Keys)}");
    }

    /// <summary>Подписка Subscriber'а на тип события (Subscribe("OrderCreated")).</summary>
    public IReadOnlyCollection<string> Subscribe(string subscriberName, string eventType)
    {
        ISubscriber subscriber = RequireSubscriber(subscriberName);
        RequireEventType(eventType);
        subscriber.Subscribe(eventType);
        foreach (EventBroker broker in _brokers) broker.Subscribe(subscriber, eventType);
        return subscriber.Subscriptions;
    }

    /// <summary>Отмена подписки Subscriber'а на тип события.</summary>
    public IReadOnlyCollection<string> Unsubscribe(string subscriberName, string eventType)
    {
        ISubscriber subscriber = RequireSubscriber(subscriberName);
        RequireEventType(eventType);
        subscriber.Unsubscribe(eventType);
        foreach (EventBroker broker in _brokers) broker.Unsubscribe(subscriberName, eventType);
        return subscriber.Subscriptions;
    }

    /// <summary>
    /// Отказ/восстановление Subscriber'а (Требование 2 ч.3): состояние меняется
    /// в программе, факт отказа фиксируется в журнале, остальные подписчики продолжают работу.
    /// </summary>
    public ISubscriber SetSubscriberState(string subscriberName, bool available)
    {
        ISubscriber subscriber = RequireSubscriber(subscriberName);
        subscriber.IsAvailable = available;
        Enqueue(new PubSubLogEntry(DateTime.Now, subscriberName,
            available ? "RECOVERED" : "UNAVAILABLE", null, null, subscriberName,
            available ? "Subscriber восстановлен (AVAILABLE)" : "Отказ Subscriber смоделирован (UNAVAILABLE)"));
        return subscriber;
    }

    /// <summary>
    /// Создать и зарегистрировать НОВОГО Subscriber'а (Части 4–5): подписки
    /// регистрируются во всех Broker'ах, Publisher НЕ изменяется (слабая связанность).
    /// kind: logger | florist | notification | analytics.
    /// </summary>
    public ISubscriber AddSubscriber(string kind, IEnumerable<string>? eventTypes = null)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        ISubscriber subscriber = kind switch
        {
            "logger" => new LoggerSubscriber(),
            "florist" or "floristservice" or "business" => new FloristServiceSubscriber(),
            "notification" or "notificationservice" => new NotificationServiceSubscriber(),
            "analytics" or "analyticsservice" => new AnalyticsServiceSubscriber(),
            _ => throw new OperationException(ErrorCodes.ValidationError,
                $"Неизвестный тип Subscriber: '{kind}'. Доступно: logger, florist, notification, analytics")
        };

        RegisterSubscriber(subscriber);

        foreach (string eventType in eventTypes ?? Array.Empty<string>())
        {
            RequireEventType(eventType);
            subscriber.Subscribe(eventType);
            foreach (EventBroker broker in _brokers) broker.Subscribe(subscriber, eventType);
        }

        Enqueue(new PubSubLogEntry(DateTime.Now, subscriber.Name, "SUBSCRIBER ADDED", null,
            null, subscriber.Name,
            $"новый Subscriber добавлен без изменения Publisher; подписки: [{string.Join(", ", subscriber.Subscriptions)}]"));
        return subscriber;
    }

    // ===== Brokers и публикация ============================================

    /// <summary>
    /// Отказ/восстановление Broker (Часть 5, Требование 1): при отказе активного
    /// Broker Publisher переключается на следующий доступный (Failover брокеров).
    /// </summary>
    public EventBroker SetBrokerState(string brokerName, bool available)
    {
        EventBroker broker = RequireBroker(brokerName);
        broker.IsAvailable = available;
        EventBroker? active = ActiveBroker;
        Enqueue(new PubSubLogEntry(DateTime.Now, broker.Name,
            available ? "AVAILABLE" : "UNAVAILABLE", null, null, null,
            available
                ? $"Broker '{broker.Name}' восстановлен (AVAILABLE)"
                : $"Broker '{broker.Name}' отключён (UNAVAILABLE); активный: {(active is null ? "нет — все Broker недоступны" : active.Name)}"));
        return broker;
    }

    /// <summary>Найти Broker или получить понятную ошибку.</summary>
    public EventBroker RequireBroker(string brokerName)
    {
        if (string.IsNullOrWhiteSpace(brokerName))
        {
            throw new OperationException(ErrorCodes.ValidationError, "Не указано имя Broker");
        }
        return _brokers.FirstOrDefault(b => string.Equals(b.Name, brokerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new OperationException(ErrorCodes.NotFound,
                $"Broker '{brokerName}' не найден. Доступны: {string.Join(", ", _brokers.Select(b => b.Name))}");
    }

    /// <summary>Создать событие предметной области с проверкой типа (обработка ошибок).</summary>
    public ShopEvent CreateEvent(string eventType, int entityId, int? clientId = null,
        IDictionary<string, string>? data = null)
    {
        RequireEventType(eventType);
        return new ShopEvent
        {
            EventType = eventType,
            EntityId = entityId,
            ClientId = clientId,
            Data = data is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Опубликовать событие: Publisher → Broker → подписанные Subscriber'ы.
    /// Publisher не обращается к Subscriber'ам напрямую (Требование 3 ч.1).
    /// </summary>
    public Task<PublishResult> PublishAsync(ShopEvent shopEvent, CancellationToken ct = default) =>
        _publisher.PublishAsync(shopEvent, ct);

    /// <summary>
    /// Повторная доставка событий из очередей всех доступных Broker'ов
    /// (Часть 5, Требование 3). Возвращает результаты по каждому Broker'у.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryResult>> RetryQueuedEventsAsync(CancellationToken ct = default)
    {
        var all = new List<DeliveryResult>();
        foreach (EventBroker broker in _brokers.Where(b => b.IsAvailable))
        {
            all.AddRange(await broker.RetryQueuedAsync(ct).ConfigureAwait(false));
        }
        return all;
    }

    // ===== Состояние и статистика ==========================================

    /// <summary>Сводное состояние: Broker'ы, подписчики, очереди (GetPubSubStatus).</summary>
    public PubSubStatus GetStatus()
    {
        EventBroker? active = ActiveBroker;
        return new PubSubStatus
        {
            ActiveBroker = active?.Name ?? "нет (все Broker недоступны)",
            BrokerCount = _brokers.Count,
            SubscriberCount = _subscribers.Count,
            QueuedEvents = _brokers.Sum(b => b.QueueCount),
            Brokers = _brokers.Select(b => new BrokerStatus
            {
                Name = b.Name,
                Available = b.IsAvailable,
                Active = b == active,
                Subscriptions = b.SubscriptionCount,
                QueuedEvents = b.QueueCount,
                RegisteredSubscribers = b.Subscribers.Count
            }).ToList(),
            Subscribers = _subscribers.Values.Select(s => new SubscriberStatus
            {
                Name = s.Name,
                Available = s.IsAvailable,
                Subscriptions = s.Subscriptions.OrderBy(x => x).ToList(),
                Received = s is SubscriberBase sb ? sb.Received.Count : 0
            }).ToList()
        };
    }

    /// <summary>Статистика по событиям на основе журнала (Требование 6 ч.5).</summary>
    public EventStatistics GetStatistics()
    {
        var publishedByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var delivered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int published = 0;
        int publishFailures = 0;

        foreach (PubSubLogEntry entry in _journal)
        {
            switch (entry.Phase)
            {
                case "PUBLISH OK" when entry.EventType is not null:
                    published++;
                    publishedByType.TryGetValue(entry.EventType, out int c);
                    publishedByType[entry.EventType] = c + 1;
                    break;
                case "PUBLISH FAILED":
                    publishFailures++;
                    break;
                case "SUCCESS" when entry.Subscriber is not null:
                    delivered.TryGetValue(entry.Subscriber, out int d);
                    delivered[entry.Subscriber] = d + 1;
                    break;
                case "QUEUED" when entry.Subscriber is not null:
                    failed.TryGetValue(entry.Subscriber, out int f);
                    failed[entry.Subscriber] = f + 1;
                    break;
            }
        }

        return new EventStatistics
        {
            Published = published,
            PublishFailures = publishFailures,
            PublishedByType = publishedByType,
            DeliveredBySubscriber = delivered,
            FailedBySubscriber = failed,
            Queued = _brokers.Sum(b => b.QueueCount)
        };
    }

    /// <summary>Журнал публикации/доставки: последние <paramref name="limit"/> записей.</summary>
    public IReadOnlyList<PubSubLogEntry> GetJournal(int? limit = null)
    {
        PubSubLogEntry[] entries = _journal.ToArray();
        if (limit is int n && n > 0 && n < entries.Length)
        {
            entries = entries[^n..];
        }
        return entries;
    }

    /// <summary>Проверка типа события: неизвестный тип — ошибка (обработка ошибок).</summary>
    private static void RequireEventType(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new OperationException(ErrorCodes.ValidationError, "Не указан тип события (идентификатор подписки)");
        }
        if (!EventTypes.IsKnown(eventType))
        {
            throw new OperationException(ErrorCodes.UnknownEventType,
                $"Неизвестный тип события '{eventType}'. Доступны: {string.Join(", ", EventTypes.All)}");
        }
    }
}