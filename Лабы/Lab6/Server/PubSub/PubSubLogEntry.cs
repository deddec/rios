namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: запись журнала публикации и доставки событий Pub/Sub.
/// Журнал фиксирует (Требование 5 ч.2): время, номер клиента, Publisher/Broker/Subscriber,
/// тип события, результат обработки и ошибку, если она возникла.
/// </summary>
public sealed record PubSubLogEntry(
    DateTime Timestamp,
    string Component,          // Publisher | Broker-1 | Logger | …
    string Phase,              // PUBLISH | EVENT RECEIVED | DELIVER | SUCCESS | UNAVAILABLE | FAILED | QUEUED | …
    int? ClientId = null,
    string? EventType = null,
    string? Subscriber = null,
    string? Detail = null)
{
    /// <summary>Строковое представление в формате журнала сервера:
    /// [14:25:10.140] Client 1 | Broker > DELIVER → Logger  [OrderCreated]  …</summary>
    public override string ToString()
    {
        string client = ClientId is int c ? $"Client {c} | " : "";
        string subscriber = Subscriber is null ? "" : $" → {Subscriber}";
        string eventType = EventType is null ? "" : $"  [{EventType}]";
        string detail = string.IsNullOrWhiteSpace(Detail) ? "" : $"  {Detail}";
        return $"[{Timestamp:HH:mm:ss.fff}] {client}{Component} > {Phase}{subscriber}{eventType}{detail}";
    }
}