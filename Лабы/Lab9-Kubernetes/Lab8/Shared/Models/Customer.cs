namespace FlowerShop.Shared.Models;

/// <summary>Клиент интернет-магазина цветов.</summary>
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
}