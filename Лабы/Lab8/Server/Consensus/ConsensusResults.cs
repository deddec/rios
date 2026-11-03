using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server.Consensus;

/// <summary>Вид голоса узла.</summary>
public enum VoteKind
{
    Yes,
    No,

    /// <summary>Узел не ответил (FAILED / обрыв связи).</summary>
    NoResponse,

    /// <summary>Узел вернул некорректный ответ — голос не учитывается.</summary>
    Invalid
}

/// <summary>Голос одного узла.</summary>
public sealed record VoteRecord(int NodeId, VoteKind Vote, string? Reason = null);

/// <summary>Результат предложения значения и голосования.</summary>
public sealed class ProposalResult
{
    public Proposal? Proposal { get; init; }
    public int? LeaderId { get; init; }
    public IReadOnlyList<VoteRecord> Votes { get; init; } = Array.Empty<VoteRecord>();
    public int Yes => Votes.Count(v => v.Vote == VoteKind.Yes);
    public int No => Votes.Count(v => v.Vote == VoteKind.No);
    public int NoResponse => Votes.Count(v => v.Vote == VoteKind.NoResponse);
    public int Majority { get; init; }
    public int TotalNodes { get; init; }

    /// <summary>true — положительных голосов не меньше большинства, решение принято и применено.</summary>
    public bool ConsensusAchieved { get; init; }

    /// <summary>Код ошибки (null, если консенсус достигнут).</summary>
    public string? ErrorCode { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>Результат выборов лидера.</summary>
public sealed class ElectionResult
{
    public int Term { get; init; }
    public int? LeaderId { get; init; }
    public IReadOnlyList<int> Participants { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> Excluded { get; init; } = Array.Empty<int>();
    public bool QuorumAvailable { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>Итог одного прохода проверки heartbeat.</summary>
public sealed class HeartbeatReport
{
    public IReadOnlyList<int> NewlyFailed { get; init; } = Array.Empty<int>();
    public bool LeaderFailed { get; init; }
    public bool ElectionTriggered { get; init; }
    public int? LeaderId { get; init; }
}

/// <summary>Параметры кластера.</summary>
public sealed class ClusterOptions
{
    /// <summary>Количество узлов (минимум 5 по заданию).</summary>
    public int NodeCount { get; init; } = 5;

    /// <summary>
    /// Сколько подряд неотвеченных heartbeat нужно, чтобы признать узел отказавшим.
    /// 1 — отказ фиксируется по первому таймауту.
    /// </summary>
    public int MissedHeartbeatsToFail { get; init; } = 1;

    /// <summary>Печатать в журнал и успешные heartbeat (ALIVE).</summary>
    public bool VerboseHeartbeats { get; init; }
}

/// <summary>Сведения о принятом решении для подписчиков кластера.</summary>
public sealed record CommittedDecision(Proposal Proposal, int Yes, int Majority, int TotalNodes, DateTime At);
