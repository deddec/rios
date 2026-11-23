using System.Collections.Concurrent;
using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №7: отслеживание подтверждений доставки (ACK) от Subscriber'ов.
/// Broker ожидает ACK от каждого подписчика перед тем, как считать событие доставленным.
/// </summary>
public sealed class EventAckTracker : IDisposable
{
    private readonly ConcurrentDictionary<string, PendingAck> _pendingAcks = new();
    private readonly object _gate = new();

    /// <summary>События, ожидающие ACK.</summary>
    public IReadOnlyList<PendingAckInfo> Pending => _pendingAcks.Values.Select(p => new PendingAckInfo(
        p.EventId, p.EventType, p.SubscriberName, p.CreatedAt, p.Timeout)).ToList();

    /// <summary>Зарегистрировать событие, которое требует ACK от указанного Subscriber'а.</summary>
    public void RegisterAck(string eventId, string eventType, string subscriberName, TimeSpan timeout)
    {
        _pendingAcks[$"{eventId}|{subscriberName}"] = new PendingAck
        {
            EventId = eventId,
            EventType = eventType,
            SubscriberName = subscriberName,
            CreatedAt = DateTime.Now,
            Timeout = timeout
        };
    }

    /// <summary>Подтвердить доставку события Subscriber'ом.</summary>
    public bool Acknowledge(string eventId, string subscriberName)
    {
        return _pendingAcks.TryRemove($"{eventId}|{subscriberName}", out _);
    }

    /// <summary>Получить события, у которых истекло время ожидания ACK.</summary>
    public IReadOnlyList<PendingAckInfo> GetTimedOut()
    {
        var now = DateTime.Now;
        return _pendingAcks.Values
            .Where(p => now - p.CreatedAt > p.Timeout)
            .Select(p => new PendingAckInfo(p.EventId, p.EventType, p.SubscriberName, p.CreatedAt, p.Timeout))
            .ToList();
    }

    /// <summary>Очистить все ожидания ACK для указанного события.</summary>
    public void ClearEvent(string eventId)
    {
        foreach (var key in _pendingAcks.Keys.Where(k => k.StartsWith($"{eventId}|", StringComparison.Ordinal)))
        {
            _pendingAcks.TryRemove(key, out _);
        }
    }

    public int PendingCount => _pendingAcks.Count;

    public void Dispose() { }
}

internal sealed class PendingAck
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string SubscriberName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public TimeSpan Timeout { get; set; }
}

/// <summary>Информация о ожидающем ACK.</summary>
public sealed record PendingAckInfo(string EventId, string EventType, string SubscriberName, DateTime CreatedAt, TimeSpan Timeout);