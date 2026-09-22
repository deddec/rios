using System.Text.Json;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>
/// Бизнес-логика предметной области "Заказ и доставка цветов" (вариант 14).
/// Операции: CreateOrder, CustomizeBouquet, AssignFlorist, ArrangeDelivery.
/// </summary>
public class BusinessLogic
{
    private readonly InMemoryDataStore _db;

    public BusinessLogic(InMemoryDataStore? db = null)
    {
        _db = db ?? new InMemoryDataStore();
    }

    /// <summary>Доступ к списку заказов для тестов и отладки.</summary>
    public IReadOnlyList<Order> DbgOrders => _db.Orders;

    /// <summary>
    /// Диспетчер операций: определяет операцию и выполняет бизнес-логику.
    /// Бросает OperationException при некорректной операции или данных.
    /// </summary>
    public Response Execute(Request request)
    {
        switch (request.Operation)
        {
            case "CreateOrder": return CreateOrder(request.Payload);
            case "CustomizeBouquet": return CustomizeBouquet(request.Payload);
            case "AssignFlorist": return AssignFlorist(request.Payload);
            case "ArrangeDelivery": return ArrangeDelivery(request.Payload);
            default:
                throw new OperationException(
                    ErrorCodes.UnknownOperation,
                    $"Неизвестная операция: '{request.Operation}'. Доступны: CreateOrder, CustomizeBouquet, AssignFlorist, ArrangeDelivery");
        }
    }
    // ----- CreateOrder -------------------------------------------------

    public Response CreateOrder(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуются данные заказа");

        int customerId = GetInt(p, "customerId");
        int bouquetId = GetInt(p, "bouquetId");
        string address = GetString(p, "deliveryAddress");
        DateTime deliveryDate = GetDate(p, "deliveryDate");

        Customer? customer = _db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer is null)
        {
            throw new OperationException(ErrorCodes.NotFound, $"Клиент с Id={customerId} не найден");
        }

        Bouquet? bouquet = _db.Bouquets.FirstOrDefault(b => b.Id == bouquetId);
        if (bouquet is null)
        {
            throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={bouquetId} не найден");
        }

        var order = new Order
        {
            Id = _db.NextOrderId(),
            CustomerId = customerId,
            BouquetId = bouquetId,
            DeliveryDate = deliveryDate,
            DeliveryAddress = address,
            Status = OrderStatus.Created
        };
        _db.Orders.Add(order);
        InMemoryDataStore.AddEvent(order, "OrderCreated");

        return Response.Ok(new { order.Id, order.Status, Bouquet = bouquet.Name, Total = bouquet.TotalPrice },
            $"Заказ #{order.Id} создан для {customer.FullName}. Сумма: {bouquet.TotalPrice} руб.");
    }

    // ----- CustomizeBouquet ---------------------------------------------

    public Response CustomizeBouquet(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуются данные букета");

        int orderId = GetInt(p, "orderId");
        Order? order = _db.Orders.FirstOrDefault(o => o.Id == orderId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Заказ с Id={orderId} не найден");

        if (order.Status == OrderStatus.Cancelled)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} отменён — изменение невозможно");
        }
        if (order.Status != OrderStatus.Created)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} уже в статусе '{order.Status}' — состав менять нельзя");
        }

        Bouquet? bouquet = _db.Bouquets.FirstOrDefault(b => b.Id == order.BouquetId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Букет с Id={order.BouquetId} не найден");

        // Замена состава букета, если передан список цветов
        if (p.TryGetProperty("flowers", out var flowersEl) && flowersEl.ValueKind == JsonValueKind.Array)
        {
            var items = new List<FlowerItem>();
            foreach (var f in flowersEl.EnumerateArray())
            {
                string name = GetString(f, "flowerName");
                int count = GetInt(f, "count");
                decimal price = GetDecimal(f, "pricePerUnit");
                items.Add(new FlowerItem { FlowerName = name, Count = count, PricePerUnit = price });
            }
            bouquet.Flowers = items;
        }

        // Изменение названия букета, если передано
        if (p.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            bouquet.Name = nameEl.GetString() ?? bouquet.Name;
        }

        if (bouquet.Flowers.Count == 0)
        {
            throw new OperationException(ErrorCodes.ValidationError, "Состав букета пуст — добавьте хотя бы один цветок");
        }

        order.Status = OrderStatus.BouquetPrepared;
        InMemoryDataStore.AddEvent(order, "BouquetPrepared");

        return Response.Ok(new { BouquetId = bouquet.Id, bouquet.Name, bouquet.Flowers, Total = bouquet.TotalPrice },
            $"Букет '{bouquet.Name}' собран. Сумма: {bouquet.TotalPrice} руб.");
    }

    // ----- AssignFlorist ------------------------------------------------

    public Response AssignFlorist(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуется Id заказа и флориста");

        int orderId = GetInt(p, "orderId");
        int floristId = GetInt(p, "floristId");

        Order? order = _db.Orders.FirstOrDefault(o => o.Id == orderId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Заказ с Id={orderId} не найден");
        if (order.Status == OrderStatus.Cancelled)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} отменён");
        }
        if (order.FloristId is not null)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"К заказу #{orderId} уже назначен флорист (Id={order.FloristId})");
        }

        Florist? florist = _db.Florists.FirstOrDefault(f => f.Id == floristId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Флорист с Id={floristId} не найден");
        if (florist.IsBusy)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Флорист {florist.FullName} сейчас занят");
        }

        order.FloristId = floristId;
        order.Status = OrderStatus.FloristAssigned;
        florist.IsBusy = true;
        InMemoryDataStore.AddEvent(order, "FloristAssigned");

        return Response.Ok(new { order.Id, FloristId = florist.Id, Florist = florist.FullName },
            $"Флорист {florist.FullName} назначен на заказ #{order.Id}");
    }

    // ----- ArrangeDelivery ----------------------------------------------

    public Response ArrangeDelivery(JsonElement? payload)
    {
        var p = payload ?? throw new OperationException(ErrorCodes.ValidationError, "Пустой запрос: требуется Id заказа");

        int orderId = GetInt(p, "orderId");
        Order? order = _db.Orders.FirstOrDefault(o => o.Id == orderId)
            ?? throw new OperationException(ErrorCodes.NotFound, $"Заказ с Id={orderId} не найден");
        if (order.Status == OrderStatus.Cancelled)
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Заказ #{orderId} отменён — доставка невозможна");
        }
        if (order.Status != OrderStatus.FloristAssigned)
        {
            throw new OperationException(ErrorCodes.ValidationError,
                $"Нельзя оформить доставку для заказа #{orderId}: сначала назначьте флориста (текущий статус: '{order.Status}')");
        }

        string address = order.DeliveryAddress;
        if (p.TryGetProperty("address", out var addrEl) && addrEl.ValueKind == JsonValueKind.String)
        {
            address = addrEl.GetString() ?? address;
        }
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new OperationException(ErrorCodes.ValidationError, "Не указан адрес доставки");
        }

        DateTime plannedAt = order.DeliveryDate;
        if (p.TryGetProperty("plannedAt", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            plannedAt = DateTime.Parse(dateEl.GetString()!);
        }

        var delivery = new Delivery
        {
            Id = _db.NextDeliveryId(),
            OrderId = order.Id,
            Address = address,
            PlannedAt = plannedAt,
            Status = "Planned"
        };
        _db.Deliveries.Add(delivery);

        order.Status = OrderStatus.DeliveryArranged;
        order.DeliveryAddress = address;
        InMemoryDataStore.AddEvent(order, "DeliveryArranged");

        // Флорист освобождается после оформления доставки
        if (order.FloristId is int fid)
        {
            var florist = _db.Florists.FirstOrDefault(f => f.Id == fid);
            if (florist is not null) florist.IsBusy = false;
        }

        return Response.Ok(new { delivery.Id, delivery.OrderId, delivery.Address, delivery.PlannedAt, delivery.Status },
            $"Доставка для заказа #{order.Id} запланирована на {plannedAt:dd.MM.yyyy HH:mm} по адресу: {address}");
    }

    // ----- Вспомогательные методы чтения параметров ----------------------

    private static int GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть целым числом");
    }

    private static decimal GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out decimal s)) return s;
        throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть числом");
    }

    private static string GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        string? s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        if (string.IsNullOrWhiteSpace(s))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' не должно быть пустым");
        }
        return s;
    }

    private static DateTime GetDate(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Отсутствует обязательное поле '{name}'");
        }
        string? s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        if (string.IsNullOrWhiteSpace(s) || !DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime date))
        {
            throw new OperationException(ErrorCodes.ValidationError, $"Поле '{name}' должно быть датой (например, 2026-09-30)");
        }
        return date;
    }
}

