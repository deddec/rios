using FlowerShop.Shared.Consensus;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server.Consensus;

/// <summary>
/// Кластер из N (≥ 5) узлов: обнаружение доступности (heartbeat), выбор лидера по правилу
/// «доступный узел с наибольшим ID», повторные выборы при отказе лидера, предложение значения,
/// голосование и принятие решения большинством (Majority = N/2 + 1).
/// Все операции, меняющие состояние, сериализуются одной блокировкой — несколько одновременных
/// запросов клиентов выполняются строго по очереди.
/// Модель намеренно проста: это не Paxos/Raft/Zab, но роли, эпохи (term), голосование и quorum
/// соответствуют их основным идеям (см. README).
/// </summary>
public sealed class ConsensusCluster
{
    private readonly object _gate = new();
    private readonly List<ClusterNode> _nodes;
    private readonly ConsensusLog _log;
    private readonly ClusterOptions _options;

    // Узлы, отказ которых уже зафиксирован в журнале (FAILED).
    private readonly HashSet<int> _reportedFailed = new();
    private readonly Dictionary<int, int> _missedHeartbeats = new();
    private readonly List<CommittedDecision> _decisions = new();

    private int _term;
    private int _electionsHeld;

    /// <summary>Вызывается после того, как решение принято большинством и применено к узлам.</summary>
    public event Action<CommittedDecision>? Committed;

    public ConsensusCluster(ConsensusLog log, ClusterOptions? options = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = options ?? new ClusterOptions();

        if (_options.NodeCount < 5)
            throw new ArgumentOutOfRangeException(nameof(options), "Минимальное количество узлов — 5");
        if (_options.MissedHeartbeatsToFail < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MissedHeartbeatsToFail должен быть ≥ 1");

        var ids = Enumerable.Range(1, _options.NodeCount).ToList();
        _nodes = ids.Select(id => new ClusterNode(id, ids)).ToList();
        foreach (int id in ids) _missedHeartbeats[id] = 0;
    }

    // ---- состояние -----------------------------------------------------------------------

    public ConsensusLog Log => _log;
    public IReadOnlyList<ClusterNode> Nodes => _nodes;
    public int TotalNodes => _nodes.Count;

    /// <summary>Большинство: N/2 + 1 (5 → 3, 4 → 3).</summary>
    public int Majority => TotalNodes / 2 + 1;

    public int AvailableCount => _nodes.Count(n => n.IsAvailable);
    public bool QuorumAvailable => AvailableCount >= Majority;
    public int Term { get { lock (_gate) return _term; } }

    /// <summary>Текущий доступный лидер (null, если лидера нет или он отказал).</summary>
    public ClusterNode? Leader => _nodes.FirstOrDefault(n => n.Role == NodeRole.Leader && n.IsAvailable);

    /// <summary>Сколько узлов одновременно имеют роль LEADER (должно быть не больше одного).</summary>
    public int LeaderRoleCount => _nodes.Count(n => n.Role == NodeRole.Leader);

    public IReadOnlyList<CommittedDecision> Decisions
    {
        get { lock (_gate) return _decisions.ToList(); }
    }

    public ClusterNode GetNode(int id) =>
        _nodes.FirstOrDefault(n => n.Id == id)
        ?? throw new ConsensusException(ErrorCodes.NotFound, $"Узел Node {id} не существует");

    // ---- запуск ----------------------------------------------------------------------------

    /// <summary>Перечислить узлы в журнале и провести первые выборы лидера.</summary>
    public ElectionResult Start()
    {
        lock (_gate)
        {
            foreach (var n in _nodes)
                _log.Write($"Node {n.Id} → {Label(n.Status)} [{Label(n.Role)}]");
            return ElectLeader();
        }
    }

    // ---- отправка сообщений -------------------------------------------------------------

    /// <summary>
    /// Маршрутизация сообщения до узла-получателя. Некорректное сообщение (null, неизвестный
    /// получатель) не роняет кластер: фиксируется в журнале; неизвестный получатель — исключение
    /// <see cref="ConsensusException"/> с кодом INVALID_MESSAGE.
    /// </summary>
    public NodeMessage? Deliver(NodeMessage? message)
    {
        if (message is null)
        {
            _log.Write("INVALID MESSAGE: пустое сообщение отклонено");
            throw new ConsensusException(ErrorCodes.InvalidMessage, "Пустое сообщение");
        }

        var target = _nodes.FirstOrDefault(n => n.Id == message.ToId);
        if (target is null)
        {
            _log.Write($"INVALID MESSAGE: получатель Node {message.ToId} не существует (от Node {message.FromId})");
            throw new ConsensusException(ErrorCodes.InvalidMessage, $"Получатель Node {message.ToId} не существует");
        }

        var response = target.Receive(message);
        if (response is { Type: MessageType.Error })
            _log.Write($"Node {target.Id} ← INVALID MESSAGE from Node {message.FromId}: {response.Reason}");

        return response;
    }

    // ---- heartbeat и обнаружение отказов --------------------------------------------------

    /// <summary>Отправить HEARTBEAT от одного узла другому. true — получен ALIVE.</summary>
    public bool SendHeartbeat(int fromId, int toId)
    {
        lock (_gate)
        {
            GetNode(fromId);
            GetNode(toId);
            _log.Write($"Node {fromId} → HEARTBEAT → Node {toId}");
            bool alive = Probe(fromId, toId);
            _log.Write(alive ? $"Node {toId} → ALIVE" : $"Node {toId} → NO RESPONSE");
            return alive;
        }
    }

    private bool Probe(int fromId, int toId)
    {
        var response = Deliver(new NodeMessage(MessageType.Heartbeat, fromId, toId, _term));
        return response is { Type: MessageType.Alive };
    }

    /// <summary>
    /// Один проход проверки доступности: лидер проверяет последователей, последователи — лидера.
    /// Узел, не ответивший на нужное число heartbeat подряд, объявляется FAILED; при отказе
    /// лидера (или отсутствии лидера) автоматически запускаются повторные выборы.
    /// </summary>
    public HeartbeatReport HeartbeatRound()
    {
        lock (_gate)
        {
            var newlyFailed = new List<int>();
            bool leaderFailed = false;
            var leaderNode = _nodes.FirstOrDefault(n => n.Role == NodeRole.Leader && !_reportedFailed.Contains(n.Id));

            foreach (var target in _nodes.Where(n => !_reportedFailed.Contains(n.Id)).ToList())
            {
                var pinger = ChoosePinger(target, leaderNode);
                if (pinger is null) continue;

                if (Probe(pinger.Id, target.Id))
                {
                    _missedHeartbeats[target.Id] = 0;
                    if (_options.VerboseHeartbeats)
                        _log.Write($"Node {pinger.Id} → HEARTBEAT → Node {target.Id}: ALIVE");
                    continue;
                }

                _log.Write($"Node {pinger.Id} → HEARTBEAT → Node {target.Id}");
                _log.Write($"Node {target.Id} → NO RESPONSE");
                if (RegisterMiss(target.Id))
                {
                    newlyFailed.Add(target.Id);
                    if (leaderNode is not null && leaderNode.Id == target.Id) leaderFailed = true;
                }
            }

            bool needElection = _term > 0 && AvailableCount > 0 && (leaderFailed || LeaderRoleCount == 0);
            if (needElection) ElectLeader();

            return new HeartbeatReport
            {
                NewlyFailed = newlyFailed,
                LeaderFailed = leaderFailed,
                ElectionTriggered = needElection,
                LeaderId = Leader?.Id
            };
        }
    }

    private ClusterNode? ChoosePinger(ClusterNode target, ClusterNode? leader)
    {
        bool usable(ClusterNode n) => n.Id != target.Id && n.IsAvailable && !_reportedFailed.Contains(n.Id);

        // Лидер проверяет последователей; если проверяется сам лидер (или лидер недоступен) —
        // проверку выполняет первый доступный последователь (например, Node 2 → HEARTBEAT → Node 5).
        if (leader is not null && leader.Id != target.Id && usable(leader)) return leader;
        return _nodes.FirstOrDefault(usable);
    }

    /// <summary>Зафиксировать неотвеченный heartbeat; true — узел только что объявлен FAILED.</summary>
    private bool RegisterMiss(int nodeId)
    {
        int missed = _missedHeartbeats[nodeId] = _missedHeartbeats.GetValueOrDefault(nodeId) + 1;
        _log.Write($"Node {nodeId} → HEARTBEAT TIMEOUT ({missed}/{_options.MissedHeartbeatsToFail})");

        if (missed < _options.MissedHeartbeatsToFail) return false;
        return MarkFailed(nodeId);
    }

    private bool MarkFailed(int nodeId)
    {
        if (!_reportedFailed.Add(nodeId)) return false;
        _log.Write($"Node {nodeId} → FAILED");
        return true;
    }

    // ---- выбор лидера ----------------------------------------------------------------------

    /// <summary>
    /// Выборы лидера: инициатор опрашивает все узлы heartbeat'ом, не ответившие исключаются,
    /// лидером становится доступный узел с максимальным ID. Остальные узлы получают роль FOLLOWER.
    /// </summary>
    public ElectionResult ElectLeader(int? initiatorId = null)
    {
        lock (_gate)
        {
            bool repeated = _electionsHeld > 0;
            _electionsHeld++;
            _log.Write(repeated ? "New election started" : "Election started");

            ClusterNode? initiator;
            if (initiatorId.HasValue)
            {
                initiator = GetNode(initiatorId.Value);
                if (!initiator.IsAvailable)
                    throw new ConsensusException(ErrorCodes.NodeUnavailable,
                        $"Node {initiator.Id} недоступен и не может инициировать выборы");
            }
            else
            {
                initiator = _nodes.FirstOrDefault(n => n.IsAvailable);
            }

            if (initiator is null)
            {
                _log.Write("Election impossible: no available nodes");
                foreach (var n in _nodes) n.SetRole(NodeRole.Follower, 0, null);
                return new ElectionResult
                {
                    Term = _term,
                    QuorumAvailable = false,
                    Message = "Нет доступных узлов — лидер не выбран"
                };
            }

            var participants = new List<int>();
            var excluded = new List<int>();
            foreach (var node in _nodes)
            {
                if (node.Id == initiator.Id || Probe(initiator.Id, node.Id))
                {
                    participants.Add(node.Id);
                    _reportedFailed.Remove(node.Id);
                    _missedHeartbeats[node.Id] = 0;
                    _log.Write($"Node {node.Id} AVAILABLE");
                }
                else
                {
                    excluded.Add(node.Id);
                    MarkFailed(node.Id);
                    _log.Write($"Node {node.Id} excluded");
                }
            }

            _term++;
            int leaderId = participants.Max();
            var leader = GetNode(leaderId);

            // Новый лидер должен знать все принятые решения: догоняет самого актуального участника.
            var freshest = participants.Select(GetNode).OrderByDescending(n => n.LastCommittedIndex).First();
            if (freshest.LastCommittedIndex > leader.LastCommittedIndex)
            {
                leader.SyncFrom(freshest);
                _log.Write($"Node {leader.Id} synchronized from Node {freshest.Id} (index {leader.LastCommittedIndex})");
            }

            foreach (var node in _nodes)
            {
                if (!participants.Contains(node.Id))
                    node.SetRole(NodeRole.Follower, 0, null); // отказавший узел не может оставаться лидером
                else if (node.Id == leaderId)
                    node.SetRole(NodeRole.Leader, _term, leaderId);
            }

            foreach (int id in participants.Where(p => p != leaderId))
            {
                var ack = Deliver(new NodeMessage(MessageType.LeaderAnnounce, leaderId, id, _term));
                if (ack is not { Type: MessageType.Ack })
                    _log.Write($"Node {id} did not acknowledge the new leader");
            }

            _log.Write($"Node {leaderId} elected as LEADER (term {_term})");

            bool quorum = participants.Count >= Majority;
            if (!quorum)
                _log.Write($"WARNING: available {participants.Count}/{TotalNodes} < majority {Majority} — решения приниматься не будут");

            return new ElectionResult
            {
                Term = _term,
                LeaderId = leaderId,
                Participants = participants,
                Excluded = excluded,
                QuorumAvailable = quorum,
                Message = $"Node {leaderId} elected as LEADER (term {_term})"
            };
        }
    }

    // ---- имитация отказов ------------------------------------------------------------------

    /// <summary>Имитация отказа: узел перестаёт отвечать на любые сообщения (в том числе heartbeat).</summary>
    public void Fail(int nodeId)
    {
        lock (_gate)
        {
            var node = GetNode(nodeId);
            if (!node.IsAvailable)
            {
                _log.Write($"Node {nodeId} уже отказал");
                return;
            }

            node.SetStatus(NodeStatus.Failed);
            _log.Write($"Node {nodeId} ({Label(node.Role)}) stops responding — failure simulated");
        }
    }

    /// <summary>
    /// Восстановление узла: он возвращается как FOLLOWER, догоняет состояние лидера.
    /// Если ID восстановленного узла больше ID лидера (или лидера нет), проводятся новые выборы.
    /// </summary>
    public ElectionResult? Recover(int nodeId)
    {
        lock (_gate)
        {
            var node = GetNode(nodeId);
            if (node.IsAvailable)
            {
                _log.Write($"Node {nodeId} уже доступен");
                return null;
            }

            node.SetStatus(NodeStatus.Available);
            node.SetRole(NodeRole.Follower, 0, null);
            _reportedFailed.Remove(nodeId);
            _missedHeartbeats[nodeId] = 0;
            _log.Write($"Node {nodeId} → AVAILABLE (recovered) [FOLLOWER]");

            var leader = Leader;
            if (leader is not null)
            {
                long before = node.LastCommittedIndex;
                node.SyncFrom(leader);
                if (node.LastCommittedIndex != before)
                    _log.Write($"Node {nodeId} synchronized from Node {leader.Id} (index {node.LastCommittedIndex})");
                node.SetRole(NodeRole.Follower, leader.Term, leader.Id);
            }

            if (leader is null || nodeId > leader.Id)
                return ElectLeader();

            return null;
        }
    }

    /// <summary>Включить/выключить для узла режим «голосовать против» (имитация расхождения).</summary>
    public void SetDissent(int nodeId, bool dissent)
    {
        lock (_gate)
        {
            GetNode(nodeId).Dissent = dissent;
            _log.Write($"Node {nodeId} dissent mode = {(dissent ? "ON (votes NO)" : "OFF")}");
        }
    }

    // ---- предложение значения и консенсус -------------------------------------------------

    /// <summary>
    /// Лидер предлагает значение, все узлы голосуют, решение принимается, если YES ≥ Majority.
    /// Если лидера нет или он отказал — сначала проводится обнаружение отказа и повторные выборы.
    /// Результат всегда возвращается объектом (исключения не используются для «консенсус не достигнут»).
    /// </summary>
    public ProposalResult Propose(ProposalKind kind, string key, string value)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return Rejected(ErrorCodes.ValidationError, "Ключ и значение предложения не должны быть пустыми");

            if (_term == 0)
                ElectLeader();

            var leader = Leader;
            if (leader is null)
            {
                // Лидер отказал: обнаружить отказ (heartbeat) и провести повторные выборы.
                HeartbeatRound();
                leader = Leader;
                if (leader is null)
                {
                    ElectLeader();
                    leader = Leader;
                }
            }

            if (leader is null)
            {
                _log.Write("Proposal rejected: no available leader");
                return Rejected(ErrorCodes.NoLeader, "Нет доступного лидера — предложение не может быть сделано");
            }

            var proposal = new Proposal(leader.LastCommittedIndex + 1, _term, kind, key, value, leader.Id);
            _log.Write($"Leader Node {leader.Id} → PROPOSE {proposal.Describe()}");

            var votes = new List<VoteRecord>();
            foreach (var node in _nodes)
            {
                votes.Add(CollectVote(leader, node, proposal));
            }

            int yes = votes.Count(v => v.Vote == VoteKind.Yes);
            int no = votes.Count(v => v.Vote == VoteKind.No);
            int silent = votes.Count(v => v.Vote == VoteKind.NoResponse);
            int majority = Majority;

            _log.Write($"YES = {yes}");
            _log.Write($"NO = {no}");
            if (silent > 0) _log.Write($"NO RESPONSE = {silent}");
            _log.Write($"Majority = {majority}");

            if (yes < majority)
            {
                _log.Write("CONSENSUS NOT ACHIEVED");
                _log.Write($"Decision {proposal.Describe()} NOT applied (YES {yes} < Majority {majority})");
                return new ProposalResult
                {
                    Proposal = proposal,
                    LeaderId = leader.Id,
                    Votes = votes,
                    Majority = majority,
                    TotalNodes = TotalNodes,
                    ConsensusAchieved = false,
                    ErrorCode = ErrorCodes.ConsensusNotAchieved,
                    Message = $"CONSENSUS NOT ACHIEVED: YES = {yes} < Majority = {majority}"
                };
            }

            _log.Write("CONSENSUS ACHIEVED");

            // Фиксация решения на всех узлах, которые ответили (отказавшие догонят при восстановлении).
            foreach (var vote in votes.Where(v => v.Vote is VoteKind.Yes or VoteKind.No))
            {
                var ack = Deliver(new NodeMessage(MessageType.Commit, leader.Id, vote.NodeId, _term, proposal));
                if (ack is not { Type: MessageType.Ack })
                    _log.Write($"Node {vote.NodeId} did not acknowledge commit");
            }

            var decision = new CommittedDecision(proposal, yes, majority, TotalNodes, DateTime.Now);
            _decisions.Add(decision);
            _log.Write($"Decision {proposal.Describe()} applied (index {proposal.Index})");

            RaiseCommitted(decision);

            return new ProposalResult
            {
                Proposal = proposal,
                LeaderId = leader.Id,
                Votes = votes,
                Majority = majority,
                TotalNodes = TotalNodes,
                ConsensusAchieved = true,
                Message = $"CONSENSUS ACHIEVED: YES = {yes}, Majority = {majority}"
            };
        }
    }

    private VoteRecord CollectVote(ClusterNode leader, ClusterNode node, Proposal proposal)
    {
        NodeMessage? response;
        try
        {
            response = Deliver(new NodeMessage(MessageType.Propose, leader.Id, node.Id, _term, proposal));
        }
        catch (ConsensusException ex)
        {
            _log.Write($"Node {node.Id} → INVALID ({ex.Message})");
            return new VoteRecord(node.Id, VoteKind.Invalid, ex.Message);
        }

        if (response is null)
        {
            // Попытка голосования недоступного узла: голос не засчитывается.
            _log.Write($"Node {node.Id} → NO RESPONSE");
            if (!_reportedFailed.Contains(node.Id)) RegisterMiss(node.Id);
            return new VoteRecord(node.Id, VoteKind.NoResponse, "узел не отвечает");
        }

        if (response is { Type: MessageType.Vote, Yes: not null })
        {
            _missedHeartbeats[node.Id] = 0;
            if (response.Yes == true)
            {
                _log.Write($"Node {node.Id} → YES");
                return new VoteRecord(node.Id, VoteKind.Yes);
            }

            _log.Write($"Node {node.Id} → NO ({response.Reason})");
            return new VoteRecord(node.Id, VoteKind.No, response.Reason);
        }

        _log.Write($"Node {node.Id} → INVALID RESPONSE ({response.Type})");
        return new VoteRecord(node.Id, VoteKind.Invalid, $"некорректный ответ {response.Type}: {response.Reason}");
    }

    private ProposalResult Rejected(string code, string message) => new()
    {
        LeaderId = Leader?.Id,
        Majority = Majority,
        TotalNodes = TotalNodes,
        ConsensusAchieved = false,
        ErrorCode = code,
        Message = message
    };

    private void RaiseCommitted(CommittedDecision decision)
    {
        var handlers = Committed;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<CommittedDecision>>())
        {
            try { handler(decision); }
            catch (Exception ex)
            {
                // Ошибка подписчика не должна откатывать уже принятое решение.
                _log.Write($"Committed handler error: {ex.Message}");
            }
        }
    }

    // ---- отчёты ------------------------------------------------------------------------------

    public ClusterStatusDto GetStatus()
    {
        lock (_gate)
        {
            var freshest = _nodes.Where(n => n.IsAvailable).OrderByDescending(n => n.LastCommittedIndex).FirstOrDefault();
            return new ClusterStatusDto
            {
                Term = _term,
                LeaderId = Leader?.Id,
                TotalNodes = TotalNodes,
                AvailableNodes = AvailableCount,
                Majority = Majority,
                QuorumAvailable = QuorumAvailable,
                ConfigurationVersion = freshest?.ConfigurationVersion ?? 0,
                LastCommittedIndex = freshest?.LastCommittedIndex ?? 0,
                Nodes = _nodes.Select(n => new NodeInfoDto
                {
                    Id = n.Id,
                    Status = Label(n.Status),
                    Role = Label(n.Role),
                    Term = n.Term,
                    KnownLeaderId = n.KnownLeaderId,
                    LastCommittedIndex = n.LastCommittedIndex,
                    ConfigurationVersion = n.ConfigurationVersion,
                    Dissent = n.Dissent
                }).ToList()
            };
        }
    }

    public static string Label(NodeStatus s) => s == NodeStatus.Available ? "AVAILABLE" : "FAILED";
    public static string Label(NodeRole r) => r == NodeRole.Leader ? "LEADER" : "FOLLOWER";
}
