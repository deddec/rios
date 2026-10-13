namespace FlowerShop.Shared.Models;

/// <summary>Флорист, который собирает букеты.</summary>
public class Florist
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;

    /// <summary>Специализация, например "свадебные букеты".</summary>
    public string? Specialty { get; set; }

    /// <summary>Занят ли флорист (назначен на заказ с доставкой).</summary>
    public bool IsBusy { get; set; }
}
