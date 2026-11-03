namespace FlowerShop.Shared.Models;

/// <summary>Статусы жизненного цикла заказа.</summary>
public static class OrderStatus
{
    public const string Created = "Created";
    public const string BouquetPrepared = "BouquetPrepared";
    public const string FloristAssigned = "FloristAssigned";
    public const string DeliveryArranged = "DeliveryArranged";
    public const string Cancelled = "Cancelled";
}

/// <summary>Заказ на букет от клиента.</summary>
public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int BouquetId { get; set; }

    /// <summary>Дата доставки (когда клиенту нужны цветы).</summary>
    public DateTime DeliveryDate { get; set; }

    public string Status { get; set; } = OrderStatus.Created;

    /// <summary>События, порождённые заказом (OrderCreated, BouquetPrepared, ...).</summary>
    public List<string> Events { get; set; } = new();

    /// <summary>Назначенный флорист (после AssignFlorist).</summary>
    public int? FloristId { get; set; }

    /// <summary>Адрес доставки.</summary>
    public string DeliveryAddress { get; set; } = string.Empty;
}