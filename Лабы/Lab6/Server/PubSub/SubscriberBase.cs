using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: базовая реализация Subscriber — подписки, искусственная задержка
/// обработки, журнал полученных событий и фиксация времени обработки.
/// Наследники определяют только реакцию на событие (<see cref="OnEventAsync"/>).
/// </summary>
public abstract class SubscriberBase : ISubscriber
{
    private readonly HashSet<string> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ShopEvent> _received = new();
    private readonly List<ProcessingTiming> _timings = new();
    private readonly object _gate = new();

    protected SubscriberBase(string name, TimeSpan? processingDelay = null)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Имя Subscriber не должно быть пустым", nameof(name))
            : name;
        ProcessingDelay = processingDelay ?? TimeSpan.Zero;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Искусственная задержка обработки события (Требование 1 ч.3):
    /// Logger — 1 с, FloristService — 3 с, NotificationService — 2 с.
    /// Позволяет показать независимую асинхронную обработку Subscriber'ами.
    /// </summary>
    public TimeSpan ProcessingDelay { get; set; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Subscriptions
    {
        get { lock (_gate) return _subscriptions.ToArray(); }
    }

    /// <summary>События, полученные и обработанные Subscriber'ом (для проверки доставки).</summary>
    public IReadOnlyList<ShopEvent> Received
    {
        get { lock (_gate) return _received.ToArray(); }
    }

    /// <summary>Зафиксированные времена начала/завершения обработки (анализ задержек, ч.5).</summary>
    public IReadOnlyList<ProcessingTiming> Timings
    {
        get { lock (_gate) return _timings.ToArray(); }
    }

    /// <inheritdoc />
    public bool IsSubscribedTo(string eventType)
    {
        lock (_gate) return _subscriptions.Contains(eventType);
    }

    /// <inheritdoc />
    public void Subscribe(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("Тип события (идентификатор подписки) не должен быть пустым", nameof(eventType));
        }
        lock (_gate) _subscriptions.Add(eventType);
    }

    /// <inheritdoc />
    public void Unsubscribe(string eventType)
    {
        lock (_gate) _subscriptions.Remove(eventType);
    }

    /// <inheritdoc />
    public async Task HandleAsync(ShopEvent shopEvent, CancellationToken ct = default)
    {
        // Отказ Subscriberа моделируется изменением состояния в программе (Требование 2 ч.3)
        if (!IsAvailable)
        {
            throw new SubscriberUnavailableException(
                $"Subscriber '{Name}' недоступен (UNAVAILABLE) — событие {shopEvent.EventType} не обработано");
        }

        DateTime startedAt = DateTime.Now;
        if (ProcessingDelay > TimeSpan.Zero)
        {
            // Искусственная задержка: обработка каждого Subscriber'а независима
            await Task.Delay(ProcessingDelay, ct).ConfigureAwait(false);
        }

        await OnEventAsync(shopEvent, ct).ConfigureAwait(false);

        DateTime finishedAt = DateTime.Now;
        lock (_gate)
        {
            _received.Add(shopEvent);
            _timings.Add(new ProcessingTiming(shopEvent.EventId, shopEvent.EventType, startedAt, finishedAt));
        }
    }

    /// <summary>Реакция конкретного Subscriber'а на событие (после искусственной задержки).</summary>
    protected virtual Task OnEventAsync(ShopEvent shopEvent, CancellationToken ct) => Task.CompletedTask;
}