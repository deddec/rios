using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6, часть 5 (Требование 4): Subscriber «AnalyticsService» — компонент аналитики.
/// НЕ изменяет данные предметной области, а только собирает статистику по событиям.
/// Подключается ДОПОЛНИТЕЛЬНО к уже работающей системе без изменения Publisher
/// (демонстрация слабой связанности Pub/Sub).
/// </summary>
public sealed class AnalyticsServiceSubscriber : SubscriberBase
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public AnalyticsServiceSubscriber(TimeSpan? processingDelay = null)
        : base("AnalyticsService", processingDelay ?? TimeSpan.FromMilliseconds(500)) { }

    /// <summary>Количество обработанных событий по типам.</summary>
    public IReadOnlyDictionary<string, int> Counts
    {
        get { lock (_gate) return new Dictionary<string, int>(_counts); }
    }

    /// <summary>Всего обработано событий.</summary>
    public int Total
    {
        get { lock (_gate) return _counts.Values.Sum(); }
    }

    protected override Task OnEventAsync(ShopEvent shopEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            _counts.TryGetValue(shopEvent.EventType, out int count);
            _counts[shopEvent.EventType] = count + 1;
        }
        return Task.CompletedTask;
    }
}