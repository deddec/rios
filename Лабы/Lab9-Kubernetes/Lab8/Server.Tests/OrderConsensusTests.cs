using FlowerShop.Server;
using FlowerShop.Server.Consensus;
using FlowerShop.Server.PubSub;
using FlowerShop.Server.Services;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;
using FlowerShop.Shared.PubSub;
using System.Text.Json;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>ЛР №7: предметная область (заказ цветов) поверх консенсуса, протокол и хранилище событий.</summary>
public class OrderConsensusTests
{
    private static (FlowerOrderService Service, ConsensusCluster Cluster) NewService(PersistentEventStore? store = null)
    {
        var cluster = new ConsensusCluster(new ConsensusLog());
        cluster.Start();
        return (new FlowerOrderService(cluster, store), cluster);
    }

    private static OrderOperationResult Create(FlowerOrderService s) =>
        s.CreateOrder(1, 2, DateTime.Today.AddDays(1), "ул. Цветочная, 5");

    [Fact]
    public void OrderLifecycle_AllowsOnlyForwardTransitions()
    {
        Assert.True(OrderLifecycle.CanTransition(null, OrderStatus.Created));
        Assert.True(OrderLifecycle.CanTransition(OrderStatus.Created, OrderStatus.BouquetPrepared));
        Assert.True(OrderLifecycle.CanTransition(OrderStatus.FloristAssigned, OrderStatus.Cancelled));
        Assert.False(OrderLifecycle.CanTransition(null, OrderStatus.BouquetPrepared));
        Assert.False(OrderLifecycle.CanTransition(OrderStatus.Created, OrderStatus.DeliveryArranged));
        Assert.False(OrderLifecycle.CanTransition(OrderStatus.Cancelled, OrderStatus.Created));
        Assert.False(OrderLifecycle.CanTransition(OrderStatus.Created, "Unknown"));
    }

    [Fact]
    public void CreateOrder_WithConsensus_CreatesOrderWithCreatedStatus()
    {
        var (service, cluster) = NewService();

        var result = Create(service);

        Assert.True(result.Success);
        Assert.Equal(OrderStatus.Created, result.Order!.Status);
        Assert.Equal(1, result.Order.Id);
        Assert.Equal(OrderStatus.Created, cluster.GetNode(1).GetOrderStatus(1));
        Assert.Equal(OrderStatus.Created, cluster.GetNode(5).GetOrderStatus(1));
    }

    [Fact]
    public void CreateOrder_WithoutMajority_OrderIsNotCreated()
    {
        var (service, cluster) = NewService();
        cluster.Fail(3);
        cluster.Fail(4);
        cluster.Fail(5);

        var result = Create(service);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConsensusNotAchieved, result.ErrorCode);
        Assert.Empty(service.ListOrders());
        Assert.Null(cluster.GetNode(1).GetOrderStatus(1));
    }

    [Fact]
    public void CreateOrder_AfterFailedAttempt_ReusesSameOrderId()
    {
        var (service, cluster) = NewService();
        cluster.Fail(3);
        cluster.Fail(4);
        cluster.Fail(5);
        Create(service);
        cluster.Recover(3);

        var result = Create(service);

        Assert.True(result.Success);
        Assert.Equal(1, result.Order!.Id);
    }

    [Fact]
    public void CreateOrder_InvalidInput_ReturnsValidationError_WithoutVoting()
    {
        var (service, cluster) = NewService();

        var noAddress = service.CreateOrder(1, 1, DateTime.Today.AddDays(1), " ");
        var badCustomer = service.CreateOrder(0, 1, DateTime.Today.AddDays(1), "адрес");
        var past = service.CreateOrder(1, 1, DateTime.Today.AddDays(-3), "адрес");

        Assert.Equal(ErrorCodes.ValidationError, noAddress.ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError, badCustomer.ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError, past.ErrorCode);
        Assert.Empty(cluster.Decisions);
    }

    [Fact]
    public void ChangeStatus_FollowsLifecycle_ThroughConsensus()
    {
        var (service, _) = NewService();
        Create(service);

        var prepared = service.ChangeStatus(1, OrderStatus.BouquetPrepared);
        var assigned = service.ChangeStatus(1, OrderStatus.FloristAssigned, floristId: 7);
        var delivery = service.ChangeStatus(1, OrderStatus.DeliveryArranged);

        Assert.True(prepared.Success && assigned.Success && delivery.Success);
        var order = service.GetOrder(1)!;
        Assert.Equal(OrderStatus.DeliveryArranged, order.Status);
        Assert.Equal(7, order.FloristId);
        Assert.Equal(new[] { "Created", "BouquetPrepared", "FloristAssigned", "DeliveryArranged" }, order.Events.ToArray());
    }

    [Fact]
    public void ChangeStatus_InvalidTransition_IsVotedDown_AndOrderUnchanged()
    {
        var (service, _) = NewService();
        Create(service);

        var result = service.ChangeStatus(1, OrderStatus.DeliveryArranged); // пропуск шагов

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConsensusNotAchieved, result.ErrorCode);
        Assert.Equal(5, result.Consensus!.No);
        Assert.Equal(OrderStatus.Created, service.GetOrder(1)!.Status);
    }

    [Fact]
    public void ChangeStatus_UnknownOrder_ReturnsNotFound()
    {
        var (service, _) = NewService();

        var result = service.ChangeStatus(99, OrderStatus.BouquetPrepared);

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public void ChangeStatus_UnknownStatus_ReturnsValidationError()
    {
        var (service, _) = NewService();
        Create(service);

        var result = service.ChangeStatus(1, "Teleported");

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public void LeaderFailure_DuringOrderLifecycle_DoesNotLoseOrders()
    {
        var (service, cluster) = NewService();
        Create(service);
        cluster.Fail(5);

        var result = service.ChangeStatus(1, OrderStatus.BouquetPrepared); // перевыборы произойдут сами

        Assert.True(result.Success);
        Assert.Equal(4, cluster.Leader!.Id);
        Assert.Equal(OrderStatus.BouquetPrepared, service.GetOrder(1)!.Status);
    }

    [Fact]
    public void RecoveredNode_CatchesUpOrderState()
    {
        var (service, cluster) = NewService();
        cluster.Fail(2);
        cluster.HeartbeatRound();
        Create(service);

        cluster.Recover(2);

        Assert.Equal(OrderStatus.Created, cluster.GetNode(2).GetOrderStatus(1));
    }

    [Fact]
    public void Consensus_PublishesShopEvent_ToPersistentStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "flowershop-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new PersistentEventStore(dir);
            var (service, _) = NewService(store);

            Create(service);
            service.ChangeStatus(1, OrderStatus.BouquetPrepared);
            service.ChangeStatus(1, OrderStatus.FloristAssigned, 3); // события для этого статуса нет

            var events = store.LoadAll();
            Assert.Equal(2, events.Count);
            Assert.Equal(EventTypes.OrderCreated, events[0].EventType);
            Assert.Equal(EventTypes.BouquetPrepared, events[1].EventType);
            Assert.Equal("1", events[0].EntityId.ToString());
            Assert.Equal("5", events[0].Data["leader"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---- протокол ------------------------------------------------------------------------------

    private static RequestRouter NewRouter(out ConsensusCluster cluster)
    {
        cluster = new ConsensusCluster(new ConsensusLog());
        cluster.Start();
        return new RequestRouter(cluster, new FlowerOrderService(cluster));
    }

    private static Request Req(string op, object? payload = null) => new()
    {
        Operation = op,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, Json.Options)
    };

    [Fact]
    public void Router_UnknownOperation_ReturnsError()
    {
        var router = NewRouter(out _);

        var response = router.Handle(Req("DoMagic"));

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.UnknownOperation, response.ErrorCode);
    }

    [Fact]
    public void Router_NullRequest_ReturnsError_WithoutThrowing()
    {
        var router = NewRouter(out _);

        Assert.Equal(ErrorCodes.UnknownOperation, router.Handle(null).ErrorCode);
    }

    [Fact]
    public void Router_FailNode_ThenHeartbeat_ThenPropose_WorksEndToEnd()
    {
        var router = NewRouter(out var cluster);

        Assert.True(router.Handle(Req("FailNode", new { nodeId = 5 })).Success);
        Assert.True(router.Handle(Req("HeartbeatRound")).Success);
        var proposed = router.Handle(Req("ProposeConfigurationVersion", new { version = 15 }));

        Assert.True(proposed.Success);
        Assert.Equal(4, cluster.Leader!.Id);
        Assert.Equal(15, cluster.GetNode(1).ConfigurationVersion);
    }

    [Fact]
    public void Router_NoMajority_ReturnsConsensusNotAchieved_WithVotesInData()
    {
        var router = NewRouter(out _);
        foreach (int id in new[] { 3, 4, 5 }) router.Handle(Req("FailNode", new { nodeId = id }));

        var response = router.Handle(Req("ProposeConfigurationVersion", new { version = 2 }));

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ConsensusNotAchieved, response.ErrorCode);
        Assert.NotNull(response.Data);
    }

    [Fact]
    public void Router_FailNode_WithoutPayload_ReturnsValidationError()
    {
        var router = NewRouter(out _);

        Assert.Equal(ErrorCodes.ValidationError, router.Handle(Req("FailNode")).ErrorCode);
    }

    [Fact]
    public void Router_FailNode_UnknownNode_ReturnsNotFound()
    {
        var router = NewRouter(out _);

        Assert.Equal(ErrorCodes.NotFound, router.Handle(Req("FailNode", new { nodeId = 42 })).ErrorCode);
    }

    [Fact]
    public void Router_CreateOrder_AndGetOrder()
    {
        var router = NewRouter(out _);

        var created = router.Handle(Req("CreateOrder", new
        {
            customerId = 1,
            bouquetId = 2,
            deliveryDate = DateTime.Today.AddDays(2),
            deliveryAddress = "ул. Роз, 1"
        }));
        var loaded = router.Handle(Req("GetOrder", new { orderId = 1 }));
        var missing = router.Handle(Req("GetOrder", new { orderId = 77 }));

        Assert.True(created.Success);
        Assert.True(loaded.Success);
        Assert.Equal(ErrorCodes.NotFound, missing.ErrorCode);
    }

    [Fact]
    public void Router_GetJournal_ReturnsRecentLines()
    {
        var router = NewRouter(out _);

        var response = router.Handle(Req("GetJournal", new { last = 5 }));

        Assert.True(response.Success);
        Assert.Equal(5, response.Data!.Value.GetArrayLength());
    }

    [Fact]
    public void Router_RunExperiment_OutOfRange_ReturnsValidationError()
    {
        var router = NewRouter(out _);

        Assert.Equal(ErrorCodes.ValidationError, router.Handle(Req("RunExperiment", new { number = 0 })).ErrorCode);
        Assert.True(router.Handle(Req("RunExperiment", new { number = 4 })).Success);
    }
}
