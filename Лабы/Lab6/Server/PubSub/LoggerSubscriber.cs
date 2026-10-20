using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: Subscriber «Logger» — записывает все подписанные события в журнал
/// (не изменяет данные предметной области). Задержка обработки — 1 секунда.
/// </summary>
public sealed class LoggerSubscriber : SubscriberBase
{
    private readonly List<string> _lines = new();

    public LoggerSubscriber(TimeSpan? processingDelay = null)
        : base("Logger", processingDelay ?? TimeSpan.FromSeconds(1)) { }

    /// <summary>Строки журнала обработанных событий.</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return _lines.ToArray(); }
    }

    protected override Task OnEventAsync(ShopEvent shopEvent, CancellationToken ct)
    {
        string client = shopEvent.ClientId is int c ? $"Client={c}" : "Client=-";
        string line = $"{shopEvent.Timestamp:HH:mm:ss.fff}  {shopEvent.EventType,-16} #{shopEvent.EntityId}  {client}  eventId={shopEvent.EventId}";
        lock (_lines) _lines.Add(line);
        return Task.CompletedTask;
    }
}