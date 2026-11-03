using System.Collections.Concurrent;
using FlowerShop.Server.Consensus;
using FlowerShop.Server.PubSub;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;
using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.Services;

/// <summary>Результат операции над заказом: заказ меняется только если консенсус достигнут.</summary>
public sealed record OrderOperationResult(
    bool Success,
    string? ErrorCode,
    string Message,
    Order? Order = null,
    ProposalResult? Consensus = null);

/// <summary>
/// Предметная область (вариант 14 — «Заказ и доставка цветов») поверх кластера консенсуса.
/// Любое изменение OrderStatus — это предложение лидера: заказ создаётся/меняет статус
/// только после того, как большинство узлов проголосовало YES. Принятое решение публикуется
/// как событие <see cref="ShopEvent"/> в постоянное хранилище (если оно задано), т.е. продолжает
/// линию Pub/Sub из предыдущих лабораторных.
/// </summary>
public sealed class FlowerOrderService
{
    private readonly ConsensusCluster _cluster;
    private readonly PersistentEventStore? _eventStore;
    private readonly ConcurrentDictionary<int, Order> _orders = new();
    private readonly ConcurrentDictionary<int, Order> _stagedOrders = new();
    private readonly ConcurrentDictionary<int, int> _stagedFlorists = new();
    private readonly object _createGate = new();
    private int _nextOrderId = 1;

    public FlowerOrderService(ConsensusCluster cluster, PersistentEventStore? eventStore = null)
    {
        _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        _eventStore = eventStore;
        _cluster.Committed += OnCommitted;
    }

    /// <summary>Создать заказ: предлагается статус Created, заказ появляется после консенсуса.</summary>
    public OrderOperationResult CreateOrder(int customerId, int bouquetId, DateTime deliveryDate, string deliveryAddress)
    {
        if (customerId <= 0) return Invalid("Некорректный Id клиента");
        if (bouquetId <= 0) return Invalid("Некорректный Id букета");
        if (string.IsNullOrWhiteSpace(deliveryAddress)) return Invalid("Адрес доставки не указан");
        if (deliveryDate.Date < DateTime.Today) return Invalid("Дата доставки не может быть в прошлом");

        lock (_createGate)
        {
            int orderId = _nextOrderId;
            _stagedOrders[orderId] = new Order
            {
                Id = orderId,
                CustomerId = customerId,
                BouquetId = bouquetId,
                DeliveryDate = deliveryDate,
                DeliveryAddress = deliveryAddress.Trim(),
                Status = OrderStatus.Created
            };

            var consensus = _cluster.Propose(ProposalKind.OrderStatus, orderId.ToString(), OrderStatus.Created);
            if (!consensus.ConsensusAchieved)
            {
                _stagedOrders.TryRemove(orderId, out _);
                return Failed(consensus);
            }

            _nextOrderId++;
            return new OrderOperationResult(true, null, $"Заказ #{orderId} создан (консенсус достигнут)",
                _orders[orderId], consensus);
        }
    }

    /// <summary>Изменить статус заказа через консенсус. Для FloristAssigned можно указать флориста.</summary>
    public OrderOperationResult ChangeStatus(int orderId, string status, int? floristId = null)
    {
        if (!_orders.TryGetValue(orderId, out var order))
            return new OrderOperationResult(false, ErrorCodes.NotFound, $"Заказ #{orderId} не найден");
        if (!OrderLifecycle.IsKnown(status))
            return Invalid($"Неизвестный статус '{status}'. Допустимые: {string.Join(", ", OrderLifecycle.AllStatuses)}");
        if (floristId is <= 0)
            return Invalid("Некорректный Id флориста");

        if (floristId.HasValue && status == OrderStatus.FloristAssigned)
            _stagedFlorists[orderId] = floristId.Value;

        var consensus = _cluster.Propose(ProposalKind.OrderStatus, orderId.ToString(), status);
        _stagedFlorists.TryRemove(orderId, out _);

        if (!consensus.ConsensusAchieved)
            return Failed(consensus, order);

        return new OrderOperationResult(true, null,
            $"Статус заказа #{orderId} изменён на {status} (консенсус достигнут)", order, consensus);
    }

    public Order? GetOrder(int orderId) => _orders.TryGetValue(orderId, out var o) ? o : null;

    public IReadOnlyList<Order> ListOrders() => _orders.Values.OrderBy(o => o.Id).ToList();

    // ---- применение принятого решения (вызывается кластером под его блокировкой) -----------

    private void OnCommitted(CommittedDecision decision)
    {
        var p = decision.Proposal;
        if (p.Kind != ProposalKind.OrderStatus || !int.TryParse(p.Key, out int orderId)) return;

        if (!_orders.TryGetValue(orderId, out var order))
        {
            if (!_stagedOrders.TryRemove(orderId, out order)) return;
            _orders[orderId] = order;
        }

        order.Status = p.Value;
        order.Events.Add(p.Value);
        if (_stagedFlorists.TryRemove(orderId, out int floristId)) order.FloristId = floristId;

        PublishEvent(order, p, decision);
    }

    private void PublishEvent(Order order, Proposal p, CommittedDecision decision)
    {
        string? eventType = p.Value switch
        {
            OrderStatus.Created => EventTypes.OrderCreated,
            OrderStatus.BouquetPrepared => EventTypes.BouquetPrepared,
            OrderStatus.DeliveryArranged => EventTypes.DeliveryArranged,
            _ => null
        };
        if (eventType is null || _eventStore is null) return;

        var shopEvent = new ShopEvent
        {
            EventType = eventType,
            EntityId = order.Id,
            ClientId = order.CustomerId,
            Data =
            {
                ["status"] = p.Value,
                ["term"] = p.Term.ToString(),
                ["index"] = p.Index.ToString(),
                ["leader"] = p.ProposerId.ToString(),
                ["votesYes"] = decision.Yes.ToString(),
                ["majority"] = decision.Majority.ToString()
            }
        };
        _eventStore.Save(shopEvent);
    }

    private static OrderOperationResult Invalid(string message) =>
        new(false, ErrorCodes.ValidationError, message);

    private static OrderOperationResult Failed(ProposalResult consensus, Order? order = null) =>
        new(false, consensus.ErrorCode ?? ErrorCodes.ConsensusNotAchieved, consensus.Message, order, consensus);
}
