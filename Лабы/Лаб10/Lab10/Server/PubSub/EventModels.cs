using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №7: результат доставки события одному Subscriber'у.
/// Status: SUCCESS | UNAVAILABLE | FAILED | ACK_PENDING; Attempts — сколько попыток предпринято.
/// </summary>
public sealed record DeliveryResult(string Subscriber, string Status, int Attempts, long ElapsedMs, string? Error = null)
{
    public bool Success => string.Equals(Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
    public bool AckPending => string.Equals(Status, "ACK_PENDING", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// ЛР №7: результат публикации события — информация, которую Publisher получает
/// от Broker (Требование 3 ч.1): где опубликовано, кому доставлено, какие ошибки.
/// </summary>
public sealed class PublishResult
{
    /// <summary>Имя Broker, принявшего событие (пусто — если ни один Broker недоступен).</summary>
    public string BrokerName { get; init; } = string.Empty;

    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;

    /// <summary>true — событие принято Broker'ом (доставка каждого Subscriber'а учитывается отдельно).</summary>
    public bool Success { get; init; }

    /// <summary>Код ошибки публикации (null при успехе).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Описание ошибки публикации (например, «все Broker недоступны»).</summary>
    public string? Error { get; init; }

    /// <summary>Результаты доставки по каждому заинтересованному Subscriber'у.</summary>
    public IReadOnlyList<DeliveryResult> Deliveries { get; init; } = Array.Empty<DeliveryResult>();

    /// <summary>Сколько Subscriber'ов успешно обработали событие.</summary>
    public int DeliveredCount => Deliveries.Count(d => d.Success);

    /// <summary>Сколько Subscriber'ов не удалось доставить (отказ/ошибка).</summary>
    public int FailedCount => Deliveries.Count - DeliveredCount;

    /// <summary>ЛР №7: событие сохранено в постоянном хранилище.</summary>
    public bool Persisted { get; init; }

    /// <summary>ЛР №7: порядковый номер события.</summary>
    public long SequenceNumber { get; init; }
}

/// <summary>
/// ЛР №7, часть 2: событие, сохранённое во временном хранении Broker
/// (очередь), — не теряется при отказе Subscriber и доставляется повторно.
/// </summary>
public sealed record PendingDelivery(ShopEvent Event, ISubscriber Subscriber, int Attempts, string LastError)
{
    public string SubscriberName => Subscriber.Name;
}

/// <summary>
/// ЛР №7: запись о подтверждении доставки (ACK) от Subscriber'а.
/// </summary>
public sealed record DeliveryAck(string EventId, string SubscriberName, DateTime AckAt);

/// <summary>
/// ЛР №7: статус события в постоянном хранилище.
/// </summary>
public enum EventStoreStatus
{
    /// <summary>Событие опубликовано, но ещё не доставлено всем подписчикам.</summary>
    Pending,
    /// <summary>Все подписчики подтвердили доставку (ACK получен).</summary>
    Delivered,
    /// <summary>Событие перемещено в DLQ (не может быть доставлено).</summary>
    DeadLetter
}