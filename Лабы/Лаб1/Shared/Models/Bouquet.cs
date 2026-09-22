namespace FlowerShop.Shared.Models;

/// <summary>Букет — композиция из цветов, оформляемая флористом.</summary>
public class Bouquet
{
    public int Id { get; set; }

    /// <summary>Название/повод букета, например "На день рождения".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Состав букета.</summary>
    public List<FlowerItem> Flowers { get; set; } = new();

    /// <summary>Стоимость букета = сумма (количество × цена) по всем цветам.</summary>
    public decimal TotalPrice => Flowers.Sum(f => f.Count * f.PricePerUnit);
}
