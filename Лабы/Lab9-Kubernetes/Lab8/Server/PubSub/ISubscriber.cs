using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №7: Subscriber (подписчик) — компонент, который подписывается на определённые
/// типы событий, получает их от Broker и обрабатывает независимо от Publisher.
/// Дополнительно к ЛР №6: поддержка подтверждения доставки (ACK).
/// </summary>
public interface ISubscriber
{
    /// <summary>Уникальное имя подписчика в журнале (Logger, FloristService, …).</summary>
    string Name { get; }

    /// <summary>Доступность подписчика: false — смоделирован отказ (AVAILABLE/UNAVAILABLE).</summary>
    bool IsAvailable { get; set; }

    /// <summary>Типы событий, на которые подписан данный Subscriber.</summary>
    IReadOnlyCollection<string> Subscriptions { get; }

    /// <summary>Подписан ли Subscriber на указанный тип события.</summary>
    bool IsSubscribedTo(string eventType);

    /// <summary>Регистрация подписки на тип события (Subscribe("OrderCreated")).</summary>
    void Subscribe(string eventType);

    /// <summary>Отмена подписки на тип события.</summary>
    void Unsubscribe(string eventType);

    /// <summary>
    /// Обработка доставленного события (вызывается Broker'ом асинхронно).
    /// Бросает <see cref="SubscriberUnavailableException"/>, если подписчик недоступен.
    /// </summary>
    Task HandleAsync(ShopEvent shopEvent, CancellationToken ct = default);

    /// <summary>
    /// ЛР №7: подтвердить получение и обработку события (ACK).
    /// Вызывается Broker'ом после успешной доставки.
    /// </summary>
    Task AcknowledgeAsync(ShopEvent shopEvent, CancellationToken ct = default);
}

/// <summary>
/// ЛР №7: отказ Subscriber при доставке события — подписчик недоступен
/// (остановлен, закрыто соединение или переведён в UNAVAILABLE).
/// Broker перехватывает исключение, фиксирует ошибку и продолжает доставку
/// остальным подписчикам (Требования 2–3 ч.3).
/// </summary>
public sealed class SubscriberUnavailableException : Exception
{
    public SubscriberUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Время начала и завершения обработки события Subscriber'ом
/// (Требование 1 ч.3 — фиксация времени независимой обработки).
/// </summary>
public sealed record ProcessingTiming(string EventId, string EventType, DateTime StartedAt, DateTime FinishedAt)
{
    public double DurationMs => (FinishedAt - StartedAt).TotalMilliseconds;
}