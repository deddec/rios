using FlowerShop.Shared.Consensus;

namespace FlowerShop.Server.Consensus;

/// <summary>Преобразование внутренних результатов в DTO протокола.</summary>
public static class ConsensusMapper
{
    public static ProposalResultDto ToDto(ProposalResult r) => new()
    {
        Index = r.Proposal?.Index ?? 0,
        Term = r.Proposal?.Term ?? 0,
        LeaderId = r.LeaderId,
        Description = r.Proposal?.Describe() ?? string.Empty,
        Votes = r.Votes.Select(v => new VoteDto
        {
            NodeId = v.NodeId,
            Vote = v.Vote switch
            {
                VoteKind.Yes => "YES",
                VoteKind.No => "NO",
                VoteKind.NoResponse => "NO_RESPONSE",
                _ => "INVALID"
            },
            Reason = v.Reason
        }).ToList(),
        Yes = r.Yes,
        No = r.No,
        NoResponse = r.NoResponse,
        Majority = r.Majority,
        TotalNodes = r.TotalNodes,
        ConsensusAchieved = r.ConsensusAchieved,
        ErrorCode = r.ErrorCode,
        Message = r.Message
    };

    public static ElectionResultDto ToDto(ElectionResult r) => new()
    {
        Term = r.Term,
        LeaderId = r.LeaderId,
        Participants = r.Participants.ToList(),
        Excluded = r.Excluded.ToList(),
        QuorumAvailable = r.QuorumAvailable,
        Message = r.Message
    };

    public static HeartbeatReportDto ToDto(HeartbeatReport r) => new()
    {
        NewlyFailed = r.NewlyFailed.ToList(),
        LeaderFailed = r.LeaderFailed,
        ElectionTriggered = r.ElectionTriggered,
        LeaderId = r.LeaderId
    };
}
