using FlowerShop.Shared.Models;

namespace FlowerShop.Server.Consensus;

/// <summary>
/// Допустимые переходы статуса заказа (предметная область — вариант 14).
/// Каждый узел проверяет предложенный лидером переход по этим правилам, прежде чем отдать голос YES.
/// </summary>
public static class OrderLifecycle
{
    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
    {
        [OrderStatus.Created] = new[] { OrderStatus.BouquetPrepared, OrderStatus.Cancelled },
        [OrderStatus.BouquetPrepared] = new[] { OrderStatus.FloristAssigned, OrderStatus.Cancelled },
        [OrderStatus.FloristAssigned] = new[] { OrderStatus.DeliveryArranged, OrderStatus.Cancelled },
        [OrderStatus.DeliveryArranged] = Array.Empty<string>(),
        [OrderStatus.Cancelled] = Array.Empty<string>()
    };

    /// <summary>Все известные статусы.</summary>
    public static IReadOnlyCollection<string> AllStatuses => Transitions.Keys;

    public static bool IsKnown(string? status) =>
        status is not null && Transitions.ContainsKey(status);

    /// <summary>
    /// Можно ли перейти в <paramref name="to"/>. <paramref name="from"/> = null — заказа ещё нет
    /// на узле: разрешено только создание (Created).
    /// </summary>
    public static bool CanTransition(string? from, string to)
    {
        if (!IsKnown(to)) return false;
        if (from is null) return to == OrderStatus.Created;
        return Transitions.TryGetValue(from, out var next) && next.Contains(to);
    }
}
