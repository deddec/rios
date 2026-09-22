namespace FlowerShop.Shared.Models;

/// <summary>Доставка заказа курьером.</summary>
public class Delivery
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string Address { get; set; } = string.Empty;

    /// <summary>Статус доставки: Planned / InProgress / Completed.</summary>
    public string Status { get; set; } = "Planned";

    /// <summary>Запланированные дата и время доставки.</summary>
    public DateTime PlannedAt { get; set; }
}
