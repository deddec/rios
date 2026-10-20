namespace FlowerShop.Server.PubSub;

/// <summary>Состояние одного Broker (для GetPubSubStatus).</summary>
public sealed class BrokerStatus
{
    public string Name { get; init; } = string.Empty;
    public bool Available { get; init; }
    public bool Active { get; init; }
    public int Subscriptions { get; init; }
    public int QueuedEvents { get; init; }
    public int RegisteredSubscribers { get; init; }
}

/// <summary>Состояние одного Subscriber (для GetPubSubStatus).</summary>
public sealed class SubscriberStatus
{
    public string Name { get; init; } = string.Empty;
    public bool Available { get; init; }
    public List<string> Subscriptions { get; init; } = new();
    public int Received { get; init; }
}

/// <summary>Сводное состояние механизма Pub/Sub (ЛР №6).</summary>
public sealed class PubSubStatus
{
    public string ActiveBroker { get; init; } = string.Empty;
    public int BrokerCount { get; init; }
    public int SubscriberCount { get; init; }
    public int QueuedEvents { get; init; }
    public List<BrokerStatus> Brokers { get; init; } = new();
    public List<SubscriberStatus> Subscribers { get; init; } = new();
}

/// <summary>Статистика по событиям (Требование 6 ч.5: статистика по событиям).</summary>
public sealed class EventStatistics
{
    /// <summary>Всего успешно опубликованных событий.</summary>
    public int Published { get; init; }

    /// <summary>Неудачных публикаций.</summary>
    public int PublishFailures { get; init; }

    /// <summary>Опубликованные события по типам.</summary>
    public Dictionary<string, int> PublishedByType { get; init; } = new();

    /// <summary>Успешные доставки по Subscriber'ам.</summary>
    public Dictionary<string, int> DeliveredBySubscriber { get; init; } = new();

    /// <summary>Недоставленные (отказ/ошибка) события по Subscriber'ам.</summary>
    public Dictionary<string, int> FailedBySubscriber { get; init; } = new();

    /// <summary>Событий ожидает повторной доставки в очередях Broker'ов.</summary>
    public int Queued { get; init; }
}