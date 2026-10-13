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

    // ===== Операции ЛР №3 (общий ресурс и синхронизация времени) ============

    /// <summary>
    /// Бронирование букета. ЛР №3: на сервере бронирование защищено критической
    /// секцией (lock или SemaphoreSlim), поэтому при одном свободном букете успех
    /// получит только один клиент.
    /// </summary>
    public static Request ReserveBouquet(int bouquetId, int? criticalDelayMs = null)
    {
        var data = new Dictionary<string, object?> { ["bouquetId"] = bouquetId };
        if (criticalDelayMs is not null) data["criticalDelayMs"] = criticalDelayMs.Value;
        return Build("ReserveBouquet", data);
    }

    /// <summary>
    /// SetBouquetAvailability — установка количества доступных экземпляров букета (общий ресурс).
    /// </summary>
    public static Request SetBouquetAvailability(int bouquetId, int count) =>
        Build("SetBouquetAvailability", new { bouquetId, count });

    /// <summary>Состояние общего ресурса: количество доступных букетов.</summary>
    public static Request GetBouquetStatus(int? bouquetId = null) =>
        Build("GetBouquetStatus", bouquetId is null ? null : new { bouquetId });

    /// <summary>
    /// GetServerTime — TIME_REQUEST из методички ЛР №3: клиент запрашивает время сервера
    /// (TIME_RESPONSE). <paramref name="delayMs"/> — искусственная задержка сервера
    /// перед фиксацией времени (эксперимент о влиянии задержки, Требование 7).
    /// </summary>
    public static Request GetServerTime(int? delayMs = null)
    {
        Dictionary<string, object?>? data = null;
        if (delayMs is not null) data = new Dictionary<string, object?> { ["delayMs"] = delayMs.Value };
        return Build("GetServerTime", data);
    }

    // ===== Операции ЛР №4 (база данных и транзакции) =========================

    /// <summary>
    /// PlaceOrderAndReserve — составная операция «создать заказ + зарезервировать букет» (ЛР №4).
    /// <paramref name="useTransaction"/>=true — в одной транзакции (COMMIT/ROLLBACK);
    /// <paramref name="simulatedError"/>=true — искусственно вызванная ошибка между операциями.
    /// </summary>
    public static Request PlaceOrderAndReserve(int customerId, int bouquetId, string deliveryAddress,
        string deliveryDate, bool useTransaction = true, bool simulatedError = false) =>
        Build("PlaceOrderAndReserve", new
        {
            customerId,
            bouquetId,
            deliveryAddress,
            deliveryDate,
            useTransaction,
            simulatedError
        });

    /// <summary>ListOrders — чтение списка заказов из БД.</summary>
    public static Request ListOrders() => Build("ListOrders");

    // ===== Операции ЛР №5 (репликация и отказоустойчивость) ================

    /// <summary>GetInstanceState — состояние Primary и Replica (AVAILABLE/UNAVAILABLE).</summary>
    public static Request GetInstanceState() => Build("GetInstanceState");

    /// <summary>CheckConsistency — проверка согласованности Primary и Replica.</summary>
    public static Request CheckConsistency() => Build("CheckConsistency");

    /// <summary>
    /// SetInstanceState — моделирование отказа/восстановления экземпляра.
    /// <paramref name="instance"/>: "primary" или "replica".
    /// <paramref name="available"/>: true — восстановить, false — смоделировать отказ.
    /// </summary>
    public static Request SetInstanceState(string instance, bool available) =>
        Build("SetInstanceState", new { instance, available });

    /// <summary>Failover — переключение на резервный экземпляр (Replica).</summary>
    public static Request Failover() => Build("Failover");

    /// <summary>Recover — восстановление Primary и синхронизация.</summary>
    public static Request Recover() => Build("Recover");

    /// <summary>Synchronize — синхронизация резервного экземпляра с активным.</summary>
    public static Request Synchronize() => Build("Synchronize");
}
