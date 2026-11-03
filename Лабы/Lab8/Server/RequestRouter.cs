using System.Text.Json;
using FlowerShop.Server.Consensus;
using FlowerShop.Server.Services;
using FlowerShop.Shared.Consensus;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>
/// Маршрутизатор операций клиент-серверного протокола. Любая ошибка превращается в
/// <see cref="Response"/> с кодом ошибки — сервер не завершается аварийно.
/// </summary>
public sealed class RequestRouter
{
    private static readonly JsonSerializerOptions ReadOptions = new(Json.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConsensusCluster _cluster;
    private readonly FlowerOrderService _orders;

    public RequestRouter(ConsensusCluster cluster, FlowerOrderService orders)
    {
        _cluster = cluster;
        _orders = orders;
    }

    /// <summary>Список поддерживаемых операций (для справки клиента).</summary>
    public static readonly IReadOnlyList<string> Operations = new[]
    {
        "Ping", "GetClusterStatus", "GetJournal", "FailNode", "RecoverNode", "SetDissent",
        "HeartbeatRound", "ElectLeader", "ProposeConfigurationVersion",
        "CreateOrder", "ChangeOrderStatus", "GetOrder", "ListOrders",
        "RunExperiment", "RunAllExperiments"
    };

    public Response Handle(Request? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Operation))
            return Response.Fail(ErrorCodes.UnknownOperation, "Операция не указана");

        try
        {
            return request.Operation.Trim().ToLowerInvariant() switch
            {
                "ping" => Response.Ok(new { Pong = true, Time = DateTime.Now }),
                "getclusterstatus" => Response.Ok(_cluster.GetStatus()),
                "getjournal" => GetJournal(request),
                "failnode" => FailNode(request),
                "recovernode" => RecoverNode(request),
                "setdissent" => SetDissent(request),
                "heartbeatround" => Response.Ok(ConsensusMapper.ToDto(_cluster.HeartbeatRound()), "Проверка heartbeat выполнена"),
                "electleader" => ElectLeader(request),
                "proposeconfigurationversion" => ProposeVersion(request),
                "createorder" => CreateOrder(request),
                "changeorderstatus" => ChangeOrderStatus(request),
                "getorder" => GetOrder(request),
                "listorders" => Response.Ok(_orders.ListOrders()),
                "runexperiment" => RunExperiment(request),
                "runallexperiments" => Response.Ok(ConsensusExperiments.RunAll()),
                _ => Response.Fail(ErrorCodes.UnknownOperation, $"Неизвестная операция '{request.Operation}'")
            };
        }
        catch (ConsensusException ex)
        {
            _cluster.Log.Write($"Request '{request.Operation}' failed: {ex.Message}");
            return Response.Fail(ex.ErrorCode, ex.Message);
        }
        catch (JsonException ex)
        {
            return Response.Fail(ErrorCodes.ValidationError, $"Некорректные данные запроса: {ex.Message}");
        }
        catch (Exception ex)
        {
            _cluster.Log.Write($"Request '{request.Operation}' internal error: {ex.Message}");
            return Response.Fail(ErrorCodes.InternalError, ex.Message);
        }
    }

    // ---- операции ----------------------------------------------------------------------------

    private Response GetJournal(Request r)
    {
        int last = Payload<JournalPayload>(r)?.Last ?? 50;
        return Response.Ok(_cluster.Log.Tail(Math.Clamp(last, 1, 1000)));
    }

    private Response FailNode(Request r)
    {
        var p = Payload<NodePayload>(r);
        if (p is null || p.NodeId <= 0) return Response.Fail(ErrorCodes.ValidationError, "Укажите nodeId");
        _cluster.Fail(p.NodeId);
        return Response.Ok(_cluster.GetStatus(), $"Node {p.NodeId} перестал отвечать (отказ будет обнаружен heartbeat'ом)");
    }

    private Response RecoverNode(Request r)
    {
        var p = Payload<NodePayload>(r);
        if (p is null || p.NodeId <= 0) return Response.Fail(ErrorCodes.ValidationError, "Укажите nodeId");
        var election = _cluster.Recover(p.NodeId);
        return Response.Ok(_cluster.GetStatus(),
            election is null ? $"Node {p.NodeId} восстановлен" : $"Node {p.NodeId} восстановлен, проведены выборы: {election.Message}");
    }

    private Response SetDissent(Request r)
    {
        var p = Payload<DissentPayload>(r);
        if (p is null || p.NodeId <= 0) return Response.Fail(ErrorCodes.ValidationError, "Укажите nodeId и dissent");
        _cluster.SetDissent(p.NodeId, p.Dissent);
        return Response.Ok(_cluster.GetStatus(), $"Node {p.NodeId}: режим «против» {(p.Dissent ? "включён" : "выключен")}");
    }

    private Response ElectLeader(Request r)
    {
        var p = Payload<NodePayload>(r);
        var result = _cluster.ElectLeader(p is { NodeId: > 0 } ? p.NodeId : null);
        return result.LeaderId is null
            ? Response.Fail(ErrorCodes.NoLeader, result.Message)
            : Response.Ok(ConsensusMapper.ToDto(result), result.Message);
    }

    private Response ProposeVersion(Request r)
    {
        var p = Payload<VersionPayload>(r);
        if (p is null || p.Version < 0) return Response.Fail(ErrorCodes.ValidationError, "Укажите version ≥ 0");

        var result = _cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", p.Version.ToString());
        return FromProposal(result);
    }

    private Response CreateOrder(Request r)
    {
        var p = Payload<CreateOrderPayload>(r);
        if (p is null) return Response.Fail(ErrorCodes.ValidationError, "Не указаны данные заказа");
        return FromOrder(_orders.CreateOrder(p.CustomerId, p.BouquetId, p.DeliveryDate, p.DeliveryAddress ?? string.Empty));
    }

    private Response ChangeOrderStatus(Request r)
    {
        var p = Payload<ChangeStatusPayload>(r);
        if (p is null) return Response.Fail(ErrorCodes.ValidationError, "Укажите orderId и status");
        return FromOrder(_orders.ChangeStatus(p.OrderId, p.Status ?? string.Empty, p.FloristId));
    }

    private Response GetOrder(Request r)
    {
        var p = Payload<OrderIdPayload>(r);
        if (p is null) return Response.Fail(ErrorCodes.ValidationError, "Укажите orderId");
        var order = _orders.GetOrder(p.OrderId);
        return order is null
            ? Response.Fail(ErrorCodes.NotFound, $"Заказ #{p.OrderId} не найден")
            : Response.Ok(order);
    }

    private Response RunExperiment(Request r)
    {
        var p = Payload<ExperimentPayload>(r);
        if (p is null || p.Number < 1 || p.Number > ConsensusExperiments.Count)
            return Response.Fail(ErrorCodes.ValidationError, $"Номер эксперимента: 1–{ConsensusExperiments.Count}");
        return Response.Ok(ConsensusExperiments.Run(p.Number));
    }

    // ---- вспомогательное --------------------------------------------------------------------

    private static Response FromProposal(ProposalResult result) =>
        result.ConsensusAchieved
            ? Response.Ok(ConsensusMapper.ToDto(result), result.Message)
            : new Response
            {
                Success = false,
                ErrorCode = result.ErrorCode ?? ErrorCodes.ConsensusNotAchieved,
                Message = result.Message,
                Data = JsonSerializer.SerializeToElement(ConsensusMapper.ToDto(result))
            };

    private static Response FromOrder(OrderOperationResult result)
    {
        if (result.Success)
            return Response.Ok(new { result.Order, Consensus = ToDto(result.Consensus) }, result.Message);

        return new Response
        {
            Success = false,
            ErrorCode = result.ErrorCode ?? ErrorCodes.InternalError,
            Message = result.Message,
            Data = result.Consensus is null
                ? null
                : JsonSerializer.SerializeToElement(new { result.Order, Consensus = ToDto(result.Consensus) })
        };
    }

    private static ProposalResultDto? ToDto(ProposalResult? r) => r is null ? null : ConsensusMapper.ToDto(r);

    private static T? Payload<T>(Request r) where T : class =>
        r.Payload is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } p
            ? p.Deserialize<T>(ReadOptions)
            : null;

    private sealed record NodePayload(int NodeId);
    private sealed record DissentPayload(int NodeId, bool Dissent);
    private sealed record VersionPayload(int Version);
    private sealed record JournalPayload(int? Last);
    private sealed record OrderIdPayload(int OrderId);
    private sealed record ExperimentPayload(int Number);
    private sealed record CreateOrderPayload(int CustomerId, int BouquetId, DateTime DeliveryDate, string? DeliveryAddress);
    private sealed record ChangeStatusPayload(int OrderId, string? Status, int? FloristId);
}
