namespace FlowerShop.Shared.Consensus;

/// <summary>ЛР №7: состояние одного узла (для клиента и журнала).</summary>
public sealed class NodeInfoDto
{
    public int Id { get; set; }

    /// <summary>AVAILABLE | FAILED.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>LEADER | FOLLOWER.</summary>
    public string Role { get; set; } = string.Empty;

    public int Term { get; set; }
    public int? KnownLeaderId { get; set; }
    public long LastCommittedIndex { get; set; }
    public int ConfigurationVersion { get; set; }

    /// <summary>Узел сконфигурирован «голосовать против» (имитация расхождения).</summary>
    public bool Dissent { get; set; }
}

/// <summary>ЛР №7: состояние кластера.</summary>
public sealed class ClusterStatusDto
{
    public int Term { get; set; }
    public int? LeaderId { get; set; }
    public int TotalNodes { get; set; }
    public int AvailableNodes { get; set; }
    public int Majority { get; set; }

    /// <summary>true — доступных узлов достаточно для большинства.</summary>
    public bool QuorumAvailable { get; set; }

    public int ConfigurationVersion { get; set; }
    public long LastCommittedIndex { get; set; }
    public List<NodeInfoDto> Nodes { get; set; } = new();
}

/// <summary>ЛР №7: голос одного узла.</summary>
public sealed class VoteDto
{
    public int NodeId { get; set; }

    /// <summary>YES | NO | NO_RESPONSE | INVALID.</summary>
    public string Vote { get; set; } = string.Empty;

    public string? Reason { get; set; }
}

/// <summary>ЛР №7: результат предложения значения и голосования.</summary>
public sealed class ProposalResultDto
{
    public long Index { get; set; }
    public int Term { get; set; }
    public int? LeaderId { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<VoteDto> Votes { get; set; } = new();
    public int Yes { get; set; }
    public int No { get; set; }
    public int NoResponse { get; set; }
    public int Majority { get; set; }
    public int TotalNodes { get; set; }
    public bool ConsensusAchieved { get; set; }
    public string? ErrorCode { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>ЛР №7: результат выборов лидера.</summary>
public sealed class ElectionResultDto
{
    public int Term { get; set; }
    public int? LeaderId { get; set; }
    public List<int> Participants { get; set; } = new();
    public List<int> Excluded { get; set; } = new();
    public bool QuorumAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>ЛР №7: результат проверки heartbeat всех узлов.</summary>
public sealed class HeartbeatReportDto
{
    public List<int> NewlyFailed { get; set; } = new();
    public bool LeaderFailed { get; set; }
    public bool ElectionTriggered { get; set; }
    public int? LeaderId { get; set; }
}

/// <summary>ЛР №7: результат одного из экспериментов (часть 6).</summary>
public sealed class ExperimentResultDto
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public int AvailableNodes { get; set; }
    public int TotalNodes { get; set; }
    public string Expected { get; set; } = string.Empty;
    public string Actual { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public List<string> Journal { get; set; } = new();
}
