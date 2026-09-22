using System.Text.Json;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Client;

/// <summary>
/// Построитель запросов: формирует Request с заданной операцией и payload.
/// </summary>
public static class RequestBuilder
{
    public static Request Build(string operation, object? payload = null) => new()
    {
        Operation = operation,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload)
    };

    public static Request CreateOrder(int customerId, int bouquetId, string deliveryAddress, string deliveryDate) =>
        Build("CreateOrder", new { customerId, bouquetId, deliveryAddress, deliveryDate });

    public static Request CustomizeBouquet(int orderId, string? name = null, object[]? flowers = null)
    {
        var data = new Dictionary<string, object?> { ["orderId"] = orderId };
        if (name is not null) data["name"] = name;
        if (flowers is not null) data["flowers"] = flowers;
        return Build("CustomizeBouquet", data);
    }

    public static Request AssignFlorist(int orderId, int floristId) =>
        Build("AssignFlorist", new { orderId, floristId });

    public static Request ArrangeDelivery(int orderId, string? address = null, string? plannedAt = null)
    {
        var data = new Dictionary<string, object?> { ["orderId"] = orderId };
        if (address is not null) data["address"] = address;
        if (plannedAt is not null) data["plannedAt"] = plannedAt;
        return Build("ArrangeDelivery", data);
    }
}
