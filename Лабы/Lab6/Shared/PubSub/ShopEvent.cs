using System;

namespace FlowerShop.Shared.PubSub;

/// <summary>
/// ЛР №6: Event (событие) модели Pub/Sub — сообщение, описывающее произошедшее
/// событие предметной области «Заказ и доставка цветов» (вариант 14).
/// Событие описывает факт произошедшего действия, а не команду к действию.
/// </summary>
public sealed class ShopEvent
{
    /// <summary>Уникальный идентификатор экземпляра события.</summary>
    public string EventId { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Тип события — одновременно идентификатор подписки (см. <see cref="EventTypes"/>):
    /// OrderCreated, BouquetPrepared, DeliveryArranged.
    /// </summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>Идентификатор объекта предметной области (например, Id заказа).</summary>
    public int EntityId { get; init; }

    /// <summary>Время создания события.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>Номер клиента, инициировавшего событие (null — если неизвестен).</summary>
    public int? ClientId { get; init; }

    /// <summary>Дополнительные данные события для Subscriber (ключ → строковое значение).</summary>
    public Dictionary<string, string> Data { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => $"{EventType} #{EntityId} (eventId={EventId})";
}