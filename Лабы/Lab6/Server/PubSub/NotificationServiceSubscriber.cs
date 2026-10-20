using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: Subscriber «NotificationService» — формирует уведомления по подписанным
/// событиям (ничего не пишет в БД предметной области). Задержка обработки — 2 секунды.
/// </summary>
public sealed class NotificationServiceSubscriber : SubscriberBase
{
    private readonly List<string> _notifications = new();

    public NotificationServiceSubscriber(TimeSpan? processingDelay = null)
        : base("NotificationService", processingDelay ?? TimeSpan.FromSeconds(2)) { }

    /// <summary>Сформированные уведомления.</summary>
    public IReadOnlyList<string> Notifications
    {
        get { lock (_notifications) return _notifications.ToArray(); }
    }

    protected override Task OnEventAsync(ShopEvent shopEvent, CancellationToken ct)
    {
        string text = shopEvent.EventType switch
        {
            EventTypes.OrderCreated => $"Уведомление: заказ #{shopEvent.EntityId} создан",
            EventTypes.BouquetPrepared => $"Уведомление: букет по заказу #{shopEvent.EntityId} готов",
            EventTypes.DeliveryArranged => $"Уведомление: доставка заказа #{shopEvent.EntityId} запланирована",
            _ => $"Уведомление: событие {shopEvent.EventType} (#{shopEvent.EntityId})"
        };

        lock (_notifications) _notifications.Add($"{DateTime.Now:HH:mm:ss.fff} {text}");
        return Task.CompletedTask;
    }
}