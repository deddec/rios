namespace FlowerShop.Shared.Models;

/// <summary>Флорист — специалист по сборке букетов.</summary>
public class Florist
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialization { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;
}