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

    public static Request CreateOrder(int customerId, int bouquetId, string deliveryAddress, string deliveryDate, int? delayMs = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["customerId"] = customerId,
            ["bouquetId"] = bouquetId,
            ["deliveryAddress"] = deliveryAddress,
            ["deliveryDate"] = deliveryDate
        };
        // ЛР №2: экспериментальная задержка обработки на сервере (Требование 4)
        if (delayMs is not null) data["delayMs"] = delayMs.Value;
        return Build("CreateOrder", data);
    }

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

    // ===== Операции ЛР №2 (общий ресурс, Race Condition) ====================

    /// <summary>Бронирование букета — эксперимент с Race Condition (Требование 6).</summary>
    public static Request ReserveBouquet(int bouquetId, int? raceDelayMs = null)
    {
        var data = new Dictionary<string, object?> { ["bouquetId"] = bouquetId };
        if (raceDelayMs is not null) data["raceDelayMs"] = raceDelayMs.Value;
        return Build("ReserveBouquet", data);
    }

    /// <summary>Установка количества доступных экземпляров букета (общий ресурс).</summary>
    public static Request SetBouquetAvailability(int bouquetId, int count) =>
        Build("SetBouquetAvailability", new { bouquetId, count });

    /// <summary>Состояние общего ресурса: количество доступных букетов.</summary>
    public static Request GetBouquetStatus(int? bouquetId = null) =>
        Build("GetBouquetStatus", bouquetId is null ? null : new { bouquetId });
}
