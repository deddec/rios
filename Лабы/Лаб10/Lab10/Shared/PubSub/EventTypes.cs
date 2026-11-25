using System.Linq;

namespace FlowerShop.Shared.PubSub;

/// <summary>
/// ЛР №7: типы событий предметной области (вариант 14 «Заказ и доставка цветов»).
/// Тип события используется и как идентификатор подписки: Subscribe("OrderCreated").
/// </summary>
public static class EventTypes
{
    /// <summary>Основное событие варианта 14 — создан заказ.</summary>
    public const string OrderCreated = "OrderCreated";

    /// <summary>Дополнительное событие — собран (подготовлен) букет.</summary>
    public const string BouquetPrepared = "BouquetPrepared";

    /// <summary>Дополнительное событие — оформлена доставка.</summary>
    public const string DeliveryArranged = "DeliveryArranged";

    /// <summary>Все типы событий варианта 14 (не менее трёх — Требование 4 ч.2).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        OrderCreated, BouquetPrepared, DeliveryArranged
    };

    /// <summary>Проверка, что тип события известен предметной области.</summary>
    public static bool IsKnown(string? eventType) =>
        !string.IsNullOrWhiteSpace(eventType) &&
        All.Any(t => string.Equals(t, eventType, StringComparison.OrdinalIgnoreCase));
}