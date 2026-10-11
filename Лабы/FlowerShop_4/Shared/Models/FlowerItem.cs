namespace FlowerShop.Shared.Models;

/// <summary>Составляющая букета: вид цветка, количество и цена за штуку.</summary>
public class FlowerItem
{
    public string FlowerName { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal PricePerUnit { get; set; }
}
