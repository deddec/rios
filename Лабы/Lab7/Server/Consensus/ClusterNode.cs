namespace FlowerShop.Server.Consensus;

/// <summary>
/// Узел распределённой системы (Требование 3): уникальный ID, состояние (AVAILABLE/FAILED),
/// роль (LEADER/FOLLOWER), знание о других узлах, приём и отправка сообщений.
/// Узел хранит собственную реплику состояния: версию конфигурации и статусы заказов.
/// Отказавший узел не отвечает ни на какие сообщения (имитация обрыва связи/остановки процесса).
/// </summary>
public sealed class ClusterNode
{
    private readonly object _gate = new();
    private readonly HashSet<int> _peerIds;
    private readonly Dictionary<int, string> _orderStatuses = new();
    private readonly List<Proposal> _committed = new();

    public int Id { get; }

    private NodeStatus _status = NodeStatus.Available;
    private NodeRole _role = NodeRole.Follower;
    private int _term;
    private int? _knownLeaderId;
    private int _configurationVersion;
    private long _lastCommittedIndex;
    private bool _dissent;
    private int _messagesReceived;

    public ClusterNode(int id, IEnumerable<int> allNodeIds)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "ID узла должен быть положительным");
        Id = id;
        _peerIds = allNodeIds.Where(x => x != id).ToHashSet();
    }

    public NodeStatus Status { get { lock (_gate) return _status; } }
    public NodeRole Role { get { lock (_gate) return _role; } }
    public int Term { get { lock (_gate) return _term; } }
    public int? KnownLeaderId { get { lock (_gate) return _knownLeaderId; } }
    public int ConfigurationVersion { get { lock (_gate) return _configurationVersion; } }
    public long LastCommittedIndex { get { lock (_gate) return _lastCommittedIndex; } }
    public int MessagesReceived { get { lock (_gate) return _messagesReceived; } }
    public bool IsAvailable => Status == NodeStatus.Available;
    public bool IsLeader => Role == NodeRole.Leader;

    /// <summary>Информация о других узлах (их ID).</summary>
    public IReadOnlyCollection<int> PeerIds => _peerIds;

    /// <summary>Если true, узел всегда отвечает NO (имитация расхождения состояния/несогласия).</summary>
    public bool Dissent
    {
        get { lock (_gate) return _dissent; }
        set { lock (_gate) _dissent = value; }
    }

    /// <summary>Копия журнала принятых решений узла.</summary>
    public IReadOnlyList<Proposal> CommittedLog
    {
        get { lock (_gate) return _committed.ToList(); }
    }

    /// <summary>Статус заказа в реплике узла (null — заказ узлу неизвестен).</summary>
    public string? GetOrderStatus(int orderId)
    {
        lock (_gate) return _orderStatuses.TryGetValue(orderId, out var s) ? s : null;
    }

    /// <summary>Реплика статусов заказов (копия).</summary>
    public IReadOnlyDictionary<int, string> OrderStatuses
    {
        get { lock (_gate) return new Dictionary<int, string>(_orderStatuses); }
    }

    // ---- изменение состояния (вызывается кластером) -------------------------------------

    internal void SetStatus(NodeStatus status)
    {
        lock (_gate) _status = status;
    }

    internal void SetRole(NodeRole role, int term, int? leaderId)
    {
        lock (_gate)
        {
            _role = role;
            if (term > _term) _term = term;
            _knownLeaderId = leaderId;
        }
    }

    /// <summary>Догнать состояние более актуального узла (после восстановления или смены лидера).</summary>
    internal void SyncFrom(ClusterNode source)
    {
        if (ReferenceEquals(source, this)) return;

        ClusterNode first = Id < source.Id ? this : source;
        ClusterNode second = ReferenceEquals(first, this) ? source : this;
        lock (first._gate)
        lock (second._gate)
        {
            if (source._lastCommittedIndex <= _lastCommittedIndex) return;
            _committed.Clear();
            _committed.AddRange(source._committed);
            _orderStatuses.Clear();
            foreach (var kv in source._orderStatuses) _orderStatuses[kv.Key] = kv.Value;
            _configurationVersion = source._configurationVersion;
            _lastCommittedIndex = source._lastCommittedIndex;
        }
    }

    // ---- обработка сообщений -----------------------------------------------------------

    /// <summary>
    /// Принять сообщение. Возвращает ответ или <c>null</c>, если узел не отвечает (FAILED).
    /// Некорректные сообщения не приводят к исключению — узел возвращает <see cref="MessageType.Error"/>.
    /// </summary>
    public NodeMessage? Receive(NodeMessage? message)
    {
        lock (_gate)
        {
            if (_status == NodeStatus.Failed) return null; // NO RESPONSE

            _messagesReceived++;

            if (message is null)
                return Error("пустое сообщение");
            if (message.ToId != Id)
                return Error($"сообщение адресовано Node {message.ToId}, а не Node {Id}");
            if (message.FromId != Id && !_peerIds.Contains(message.FromId))
                return Error($"неизвестный отправитель Node {message.FromId}");

            switch (message.Type)
            {
                case MessageType.Heartbeat:
                    return new NodeMessage(MessageType.Alive, Id, message.FromId, _term);

                case MessageType.LeaderAnnounce:
                    return HandleLeaderAnnounce(message);

                case MessageType.Propose:
                    return HandlePropose(message);

                case MessageType.Commit:
                    return HandleCommit(message);

                default:
                    return Error($"неожиданный тип сообщения {message.Type}");
            }
        }
    }

    private NodeMessage HandleLeaderAnnounce(NodeMessage m)
    {
        if (m.Term < _term)
            return Error($"устаревшая эпоха {m.Term} (текущая {_term})", m.FromId);

        _term = m.Term;
        _knownLeaderId = m.FromId;
        _role = m.FromId == Id ? NodeRole.Leader : NodeRole.Follower;
        return new NodeMessage(MessageType.Ack, Id, m.FromId, _term);
    }

    private NodeMessage HandlePropose(NodeMessage m)
    {
        if (m.Proposal is null)
            return Error("предложение без значения", m.FromId);

        var (yes, reason) = Evaluate(m.Proposal);
        return new NodeMessage(MessageType.Vote, Id, m.FromId, _term, m.Proposal, yes, reason);
    }

    private NodeMessage HandleCommit(NodeMessage m)
    {
        if (m.Proposal is null)
            return Error("фиксация без значения", m.FromId);

        Apply(m.Proposal);
        return new NodeMessage(MessageType.Ack, Id, m.FromId, _term);
    }

    /// <summary>Решение узла: голосовать YES или NO (вызывается под блокировкой).</summary>
    private (bool Yes, string? Reason) Evaluate(Proposal p)
    {
        if (_dissent) return (false, "узел настроен голосовать против");
        if (p.Term < _term) return (false, $"устаревшая эпоха {p.Term} < {_term}");
        if (p.Index <= _lastCommittedIndex) return (false, $"устаревший индекс {p.Index} <= {_lastCommittedIndex}");

        switch (p.Kind)
        {
            case ProposalKind.ConfigurationVersion:
                if (!int.TryParse(p.Value, out int version) || version < 0)
                    return (false, $"некорректная версия '{p.Value}'");
                if (version <= _configurationVersion)
                    return (false, $"версия {version} не больше текущей {_configurationVersion}");
                return (true, null);

            case ProposalKind.OrderStatus:
                if (!int.TryParse(p.Key, out int orderId) || orderId <= 0)
                    return (false, $"некорректный Id заказа '{p.Key}'");
                if (!OrderLifecycle.IsKnown(p.Value))
                    return (false, $"неизвестный статус '{p.Value}'");
                _orderStatuses.TryGetValue(orderId, out var current);
                if (!OrderLifecycle.CanTransition(current, p.Value))
                    return (false, $"переход {current ?? "(нет заказа)"} -> {p.Value} недопустим");
                return (true, null);

            default:
                return (false, $"неизвестный вид предложения {p.Kind}");
        }
    }

    /// <summary>Применить принятое решение к реплике (вызывается под блокировкой).</summary>
    private void Apply(Proposal p)
    {
        if (p.Index <= _lastCommittedIndex) return; // повторная фиксация — игнорируем (идемпотентность)

        switch (p.Kind)
        {
            case ProposalKind.ConfigurationVersion when int.TryParse(p.Value, out int v):
                _configurationVersion = v;
                break;
            case ProposalKind.OrderStatus when int.TryParse(p.Key, out int orderId):
                _orderStatuses[orderId] = p.Value;
                break;
        }

        _committed.Add(p);
        _lastCommittedIndex = p.Index;
    }

    private NodeMessage Error(string reason, int? to = null) =>
        new(MessageType.Error, Id, to ?? Id, _term, Reason: reason);

    public override string ToString() => $"Node {Id} [{_status}/{_role}]";
}
