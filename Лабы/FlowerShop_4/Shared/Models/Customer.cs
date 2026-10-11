namespace FlowerShop.Shared.Models;

/// <summary>Клиент цветочного магазина.</summary>
public class Customer
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Address { get; set; }
}
