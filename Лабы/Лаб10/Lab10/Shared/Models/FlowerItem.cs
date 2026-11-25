namespace FlowerShop.Shared.Models;

/// <summary>Один вид цветов в составе букета.</summary>
public class FlowerItem
{
    public string FlowerName { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal PricePerUnit { get; set; }
}