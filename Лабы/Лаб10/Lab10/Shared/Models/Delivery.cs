namespace FlowerShop.Shared.Models;

/// <summary>Информация о доставке заказа.</summary>
public class Delivery
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string Address { get; set; } = string.Empty;
    public DateTime PlannedAt { get; set; }
    public string Status { get; set; } = "Planned";
}