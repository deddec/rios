using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: Subscriber «FloristService» (роль BusinessService) — выполняет изменение
/// состояния предметной области: отслеживает этап подготовки заказа по событиям.
/// Задержка обработки — 3 секунды (самый «медленный» подписчик для демонстрации
/// независимой асинхронной обработки).
/// </summary>
public sealed class FloristServiceSubscriber : SubscriberBase
{
    private readonly Dictionary<int, string> _orderStates = new();
    private readonly object _gate = new();

    public FloristServiceSubscriber(TimeSpan? processingDelay = null)
        : base("FloristService", processingDelay ?? TimeSpan.FromSeconds(3)) { }

    /// <summary>Состояния заказов, обновлённые на основе событий.</summary>
    public IReadOnlyDictionary<int, string> OrderStates
    {
        get { lock (_gate) return new Dictionary<int, string>(_orderStates); }
    }

    protected override Task OnEventAsync(ShopEvent shopEvent, CancellationToken ct)
    {
        string state = shopEvent.EventType switch
        {
            EventTypes.OrderCreated => "Принят — подготовка букета",
            EventTypes.BouquetPrepared => "Букет собран",
            EventTypes.DeliveryArranged => "Готов к доставке",
            _ => "Обработано"
        };

        lock (_gate) _orderStates[shopEvent.EntityId] = state;
        return Task.CompletedTask;
    }
}