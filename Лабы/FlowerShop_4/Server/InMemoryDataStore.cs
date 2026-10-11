using System.Text.Json;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>
/// Временное хранилище данных (Требование 6: БД не обязательна — коллекции/списки).
/// Данные предзаполнены демонстрационными объектами.
/// </summary>
public class InMemoryDataStore
{
    public List<Customer> Customers { get; } = new();
    public List<Bouquet> Bouquets { get; } = new();
    public List<Order> Orders { get; } = new();
    public List<Florist> Florists { get; } = new();
    public List<Delivery> Deliveries { get; } = new();

    private int _nextCustomerId = 1;
    private int _nextBouquetId = 1;
    private int _nextOrderId = 1;
    private int _nextFloristId = 1;
    private int _nextDeliveryId = 1;

    public InMemoryDataStore()
    {
        // Демонстрационные данные
        Customers.Add(new Customer { Id = NextCustomerId(), FullName = "Иванова Анна", Phone = "+7-912-345-67-89", Address = "ул. Ленина, 10" });
        Customers.Add(new Customer { Id = NextCustomerId(), FullName = "Петров Сергей", Phone = "+7-923-456-78-90", Address = "пр. Мира, 25" });

        Bouquets.Add(new Bouquet
        {
            Id = NextBouquetId(),
            Name = "Розовое облако",
            AvailableCount = 3,
            Flowers = { new FlowerItem { FlowerName = "Роза", Count = 7, PricePerUnit = 120m }, new FlowerItem { FlowerName = "Эвкалипт", Count = 3, PricePerUnit = 40m } }
        });
        Bouquets.Add(new Bouquet
        {
            Id = NextBouquetId(),
            Name = "Весенний",
            AvailableCount = 5,
            Flowers = { new FlowerItem { FlowerName = "Тюльпан", Count = 15, PricePerUnit = 60m } }
        });
        // Букет для эксперимента ЛР №2: количество общего ресурса = 1
        Bouquets.Add(new Bouquet
        {
            Id = NextBouquetId(),
            Name = "Новогодний люкс",
            AvailableCount = 1,
            Flowers = { new FlowerItem { FlowerName = "Роза", Count = 15, PricePerUnit = 200m } }
        });

        Florists.Add(new Florist { Id = NextFloristId(), FullName = "Смирнова Ольга", Specialty = "свадебные букеты" });
        Florists.Add(new Florist { Id = NextFloristId(), FullName = "Кузнецова Мария", Specialty = "букеты на день рождения" });
    }

    public int NextCustomerId() => _nextCustomerId++;
    public int NextBouquetId() => _nextBouquetId++;
    public int NextOrderId() => _nextOrderId++;
    public int NextFloristId() => _nextFloristId++;
    public int NextDeliveryId() => _nextDeliveryId++;

    /// <summary>Добавление события в историю заказа (OrderCreated, BouquetPrepared и т.д.).</summary>
    public static void AddEvent(Order order, string @event)
    {
        order.Events.Add(@event);
        Console.WriteLine($"[Сервер] Событие: {@event} (заказ #{order.Id})");
    }
}
