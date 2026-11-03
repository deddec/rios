using FlowerShop.Server.Consensus;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>ЛР №7, Требование 4: unit-тесты узлов, выбора лидера, голосования и консенсуса.</summary>
public class ConsensusClusterTests
{
    private static (ConsensusCluster Cluster, ConsensusLog Log) NewCluster(int missed = 1, int nodes = 5)
    {
        var log = new ConsensusLog();
        var cluster = new ConsensusCluster(log, new ClusterOptions { NodeCount = nodes, MissedHeartbeatsToFail = missed });
        return (cluster, log);
    }

    private static (ConsensusCluster Cluster, ConsensusLog Log) Started()
    {
        var (cluster, log) = NewCluster();
        cluster.Start();
        return (cluster, log);
    }

    private static ProposalResult ProposeVersion(ConsensusCluster c, int version) =>
        c.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", version.ToString());

    // ---- создание узлов ------------------------------------------------------------------

    [Fact]
    public void Cluster_CreatesFiveNodes_WithInitialAvailableFollowerState()
    {
        var (cluster, _) = NewCluster();

        Assert.Equal(5, cluster.Nodes.Count);
        Assert.Equal(5, cluster.AvailableCount);
        Assert.True(cluster.Nodes.All(n => n.Status == NodeStatus.Available));
        Assert.True(cluster.Nodes.All(n => n.Role == NodeRole.Follower));
        Assert.Null(cluster.Leader);
    }

    [Fact]
    public void Cluster_RejectsFewerThanFiveNodes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewCluster(nodes: 4));
    }

    [Fact]
    public void Nodes_HaveUniqueIds()
    {
        var (cluster, _) = NewCluster();

        var ids = cluster.Nodes.Select(n => n.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact]
    public void Node_KnowsAboutAllOtherNodes()
    {
        var (cluster, _) = NewCluster();

        var peers = cluster.GetNode(3).PeerIds.OrderBy(x => x).ToList();

        Assert.Equal(new[] { 1, 2, 4, 5 }, peers);
    }

    [Fact]
    public void GetNode_UnknownId_Throws()
    {
        var (cluster, _) = NewCluster();

        var ex = Assert.Throws<ConsensusException>(() => cluster.GetNode(99));
        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
    }

    // ---- доступные узлы ------------------------------------------------------------------

    [Fact]
    public void AvailableCount_DropsWhenNodeFails()
    {
        var (cluster, _) = Started();

        cluster.Fail(2);
        cluster.Fail(3);

        Assert.Equal(3, cluster.AvailableCount);
        Assert.Equal(NodeStatus.Failed, cluster.GetNode(2).Status);
    }

    [Fact]
    public void FailedNode_DoesNotRespondToHeartbeat()
    {
        var (cluster, log) = Started();
        cluster.Fail(5);

        bool alive = cluster.SendHeartbeat(2, 5);

        Assert.False(alive);
        Assert.True(log.Contains("Node 2 → HEARTBEAT → Node 5"));
        Assert.True(log.Contains("Node 5 → NO RESPONSE"));
    }

    [Fact]
    public void AvailableNode_RespondsToHeartbeatWithAlive()
    {
        var (cluster, _) = Started();

        Assert.True(cluster.SendHeartbeat(2, 5));
    }

    // ---- выбор лидера --------------------------------------------------------------------

    [Fact]
    public void Election_ChoosesAvailableNodeWithHighestId()
    {
        var (cluster, log) = NewCluster();

        var result = cluster.Start();

        Assert.Equal(5, result.LeaderId);
        Assert.Equal(5, cluster.Leader!.Id);
        Assert.True(log.Contains("Election started"));
        Assert.True(log.Contains("Node 5 elected as LEADER"));
    }

    [Fact]
    public void Election_NeverElectsUnavailableNode()
    {
        var (cluster, _) = NewCluster();
        cluster.Fail(5);
        cluster.Fail(4);

        var result = cluster.ElectLeader();

        Assert.Equal(3, result.LeaderId);
        Assert.Equal(new[] { 4, 5 }, result.Excluded.ToArray());
        Assert.True(cluster.GetNode(5).Role != NodeRole.Leader);
    }

    [Fact]
    public void Election_OnlyOneLeaderExists()
    {
        var (cluster, _) = Started();

        Assert.Equal(1, cluster.LeaderRoleCount);
        Assert.Equal(4, cluster.Nodes.Count(n => n.Role == NodeRole.Follower));
    }

    [Fact]
    public void Election_InitiatedByFailedNode_Throws()
    {
        var (cluster, _) = Started();
        cluster.Fail(2);

        var ex = Assert.Throws<ConsensusException>(() => cluster.ElectLeader(2));
        Assert.Equal(ErrorCodes.NodeUnavailable, ex.ErrorCode);
    }

    [Fact]
    public void Election_IncrementsTerm_AndFollowersKnowLeader()
    {
        var (cluster, _) = Started();

        Assert.Equal(1, cluster.Term);
        Assert.True(cluster.Nodes.All(n => n.KnownLeaderId == 5 && n.Term == 1));
    }

    // ---- обнаружение отказа и повторные выборы ----------------------------------------------

    [Fact]
    public void HeartbeatRound_DetectsLeaderFailure_AndLogsIt()
    {
        var (cluster, log) = Started();
        cluster.Fail(5);

        var report = cluster.HeartbeatRound();

        Assert.True(report.LeaderFailed);
        Assert.Contains(5, report.NewlyFailed);
        Assert.True(log.Contains("Node 5 → HEARTBEAT TIMEOUT"));
        Assert.True(log.Contains("Node 5 → FAILED"));
    }

    [Fact]
    public void LeaderFailure_TriggersReelection_NewLeaderIsNode4()
    {
        var (cluster, log) = Started();
        cluster.Fail(5);

        var report = cluster.HeartbeatRound();

        Assert.True(report.ElectionTriggered);
        Assert.Equal(4, report.LeaderId);
        Assert.Equal(4, cluster.Leader!.Id);
        Assert.Equal(1, cluster.LeaderRoleCount);
        Assert.True(log.Contains("New election started"));
        Assert.True(log.Contains("Node 5 excluded"));
        Assert.True(log.Contains("Node 4 elected as LEADER"));
    }

    [Fact]
    public void FailedLeader_IsNoLongerLeaderAfterReelection()
    {
        var (cluster, _) = Started();
        cluster.Fail(5);
        cluster.HeartbeatRound();

        Assert.Equal(NodeStatus.Failed, cluster.GetNode(5).Status);
        Assert.Equal(NodeRole.Follower, cluster.GetNode(5).Role);
    }

    [Fact]
    public void FollowerFailure_DoesNotChangeLeader()
    {
        var (cluster, _) = Started();
        cluster.Fail(2);

        var report = cluster.HeartbeatRound();

        Assert.False(report.LeaderFailed);
        Assert.False(report.ElectionTriggered);
        Assert.Contains(2, report.NewlyFailed);
        Assert.Equal(5, cluster.Leader!.Id);
    }

    [Fact]
    public void FailureIsReportedOnlyOnce_AcrossHeartbeatRounds()
    {
        var (cluster, log) = Started();
        cluster.Fail(2);

        cluster.HeartbeatRound();
        var second = cluster.HeartbeatRound();

        Assert.Empty(second.NewlyFailed);
        Assert.Equal(1, log.Lines.Count(l => l.Contains("Node 2 → FAILED")));
    }

    [Fact]
    public void FailureDetection_RequiresConfiguredNumberOfMissedHeartbeats()
    {
        var (cluster, _) = NewCluster(missed: 2);
        cluster.Start();
        cluster.Fail(3);

        var first = cluster.HeartbeatRound();
        var second = cluster.HeartbeatRound();

        Assert.Empty(first.NewlyFailed);
        Assert.Contains(3, second.NewlyFailed);
    }

    [Fact]
    public void Propose_AfterLeaderFailure_ReelectsAutomatically()
    {
        var (cluster, _) = Started();
        cluster.Fail(5); // отказ обнаруживается самим предложением, без явного HeartbeatRound

        var result = ProposeVersion(cluster, 1);

        Assert.True(result.ConsensusAchieved);
        Assert.Equal(4, result.LeaderId);
    }

    [Fact]
    public void Recovery_OfHigherIdNode_TriggersElection_AndNodeCatchesUp()
    {
        var (cluster, _) = Started();
        cluster.Fail(5);
        cluster.HeartbeatRound();
        ProposeVersion(cluster, 7); // пока Node 5 недоступен

        var election = cluster.Recover(5);

        Assert.NotNull(election);
        Assert.Equal(5, cluster.Leader!.Id);
        Assert.Equal(7, cluster.GetNode(5).ConfigurationVersion);
        Assert.Equal(1, cluster.LeaderRoleCount);
    }

    [Fact]
    public void Recovery_OfLowerIdNode_KeepsLeader()
    {
        var (cluster, _) = Started();
        cluster.Fail(2);
        cluster.HeartbeatRound();

        var election = cluster.Recover(2);

        Assert.Null(election);
        Assert.Equal(5, cluster.Leader!.Id);
        Assert.Equal(NodeStatus.Available, cluster.GetNode(2).Status);
    }

    // ---- большинство ----------------------------------------------------------------------

    [Theory]
    [InlineData(5, 3)]
    [InlineData(6, 4)]
    [InlineData(7, 4)]
    public void Majority_IsHalfPlusOne(int nodes, int expected)
    {
        var (cluster, _) = NewCluster(nodes: nodes);

        Assert.Equal(expected, cluster.Majority);
    }

    [Fact]
    public void Quorum_IsAvailable_OnlyWhileMajorityOfNodesIsAvailable()
    {
        var (cluster, _) = Started();

        Assert.True(cluster.QuorumAvailable);   // 5
        cluster.Fail(5);
        Assert.True(cluster.QuorumAvailable);   // 4
        cluster.Fail(4);
        Assert.True(cluster.QuorumAvailable);   // 3
        cluster.Fail(3);
        Assert.False(cluster.QuorumAvailable);  // 2
    }

    // ---- голосование и консенсус ------------------------------------------------------------

    [Fact]
    public void Voting_AllYes_ConsensusAchieved()
    {
        var (cluster, log) = Started();

        var result = ProposeVersion(cluster, 10);

        Assert.True(result.ConsensusAchieved);
        Assert.Equal(5, result.Yes);
        Assert.Equal(0, result.No);
        Assert.Equal(3, result.Majority);
        Assert.Null(result.ErrorCode);
        Assert.True(log.Contains("PROPOSE Version=10"));
        Assert.True(log.Contains("YES = 5"));
        Assert.True(log.Contains("Majority = 3"));
        Assert.True(log.Contains("CONSENSUS ACHIEVED"));
    }

    [Fact]
    public void Voting_DecisionIsAppliedToAllNodes()
    {
        var (cluster, _) = Started();

        ProposeVersion(cluster, 10);

        Assert.True(cluster.Nodes.All(n => n.ConfigurationVersion == 10));
        Assert.True(cluster.Nodes.All(n => n.LastCommittedIndex == 1));
    }

    [Fact]
    public void Voting_OneNegativeVote_StillAchievesConsensus()
    {
        var (cluster, log) = Started();
        cluster.SetDissent(4, true);

        var result = ProposeVersion(cluster, 12);

        Assert.True(result.ConsensusAchieved);
        Assert.Equal(4, result.Yes);
        Assert.Equal(1, result.No);
        Assert.True(log.Contains("Node 4 → NO"));
    }

    [Fact]
    public void Voting_NegativeMajority_NoConsensus_AndValueNotApplied()
    {
        var (cluster, log) = Started();
        cluster.SetDissent(3, true);
        cluster.SetDissent(4, true);
        cluster.SetDissent(5, true);

        var result = ProposeVersion(cluster, 12);

        Assert.False(result.ConsensusAchieved);
        Assert.Equal(2, result.Yes);
        Assert.Equal(3, result.No);
        Assert.Equal(ErrorCodes.ConsensusNotAchieved, result.ErrorCode);
        Assert.True(cluster.Nodes.All(n => n.ConfigurationVersion == 0));
        Assert.True(log.Contains("CONSENSUS NOT ACHIEVED"));
    }

    [Fact]
    public void Voting_EqualSplit_IsNotConsensus()
    {
        // 6 узлов: 3 против 3 — большинство (4) не набрано.
        var (cluster, _) = NewCluster(nodes: 6);
        cluster.Start();
        cluster.SetDissent(4, true);
        cluster.SetDissent(5, true);
        cluster.SetDissent(6, true);

        var result = ProposeVersion(cluster, 1);

        Assert.Equal(3, result.Yes);
        Assert.Equal(3, result.No);
        Assert.False(result.ConsensusAchieved);
    }

    [Fact]
    public void Consensus_WithFourOfFive_Achieved()
    {
        var (cluster, _) = Started();
        cluster.Fail(5);
        cluster.HeartbeatRound();

        var result = ProposeVersion(cluster, 11);

        Assert.True(result.ConsensusAchieved);
        Assert.Equal(4, result.Yes);
        Assert.Equal(1, result.NoResponse);
    }

    [Fact]
    public void Consensus_WithThreeOfFive_Achieved()
    {
        var (cluster, _) = Started();
        cluster.Fail(4);
        cluster.Fail(5);
        cluster.HeartbeatRound();

        var result = ProposeVersion(cluster, 12);

        Assert.True(result.ConsensusAchieved);
        Assert.Equal(3, result.Yes);
        Assert.Equal(3, result.LeaderId);
    }

    [Fact]
    public void Consensus_WithTwoOfFive_NotAchieved_AndNothingIsApplied()
    {
        var (cluster, log) = Started();
        cluster.Fail(3);
        cluster.Fail(4);
        cluster.Fail(5);
        cluster.HeartbeatRound();

        var result = ProposeVersion(cluster, 13);

        Assert.False(result.ConsensusAchieved);
        Assert.Equal(2, result.Yes);
        Assert.Equal(3, result.NoResponse);
        Assert.Equal(ErrorCodes.ConsensusNotAchieved, result.ErrorCode);
        Assert.True(cluster.Nodes.All(n => n.ConfigurationVersion == 0));
        Assert.Empty(cluster.Decisions);
        Assert.True(log.Contains("CONSENSUS NOT ACHIEVED"));
    }

    [Fact]
    public void Voting_FailedNodeVote_IsNotCounted()
    {
        var (cluster, _) = Started();
        cluster.Fail(1);

        var result = ProposeVersion(cluster, 5);

        var vote = result.Votes.Single(v => v.NodeId == 1);
        Assert.Equal(VoteKind.NoResponse, vote.Vote);
        Assert.Equal(4, result.Yes);
    }

    [Fact]
    public void Voting_StaleVersion_IsRejectedByNodes()
    {
        var (cluster, _) = Started();
        ProposeVersion(cluster, 10);

        var result = ProposeVersion(cluster, 9); // версия не больше текущей

        Assert.False(result.ConsensusAchieved);
        Assert.Equal(5, result.No);
        Assert.Equal(10, cluster.Leader!.ConfigurationVersion);
    }

    [Fact]
    public void Propose_EmptyValue_ReturnsValidationError_WithoutVoting()
    {
        var (cluster, _) = Started();

        var result = cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", " ");

        Assert.False(result.ConsensusAchieved);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Empty(result.Votes);
    }

    // ---- работа после смены лидера ----------------------------------------------------------

    [Fact]
    public void AfterLeaderChange_NewLeaderContinuesDecisionSequence()
    {
        var (cluster, _) = Started();
        ProposeVersion(cluster, 10);
        cluster.Fail(5);
        cluster.HeartbeatRound();

        var result = ProposeVersion(cluster, 11);

        Assert.True(result.ConsensusAchieved);
        Assert.Equal(4, result.LeaderId);
        Assert.Equal(2, result.Proposal!.Index);   // нумерация решений продолжается
        Assert.Equal(2, result.Proposal.Term);     // новая эпоха
        Assert.Equal(2, cluster.Decisions.Count);
        Assert.Equal(11, cluster.GetNode(1).ConfigurationVersion);
    }

    [Fact]
    public void DecisionsAreOrdered_AndIndexesAreConsecutive()
    {
        var (cluster, _) = Started();

        for (int v = 1; v <= 4; v++) ProposeVersion(cluster, v);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, cluster.Decisions.Select(d => d.Proposal.Index).ToArray());
    }

    // ---- ошибки и сообщения -------------------------------------------------------------

    [Fact]
    public void AllNodesFailed_ElectionDoesNotCrash_AndNoLeader()
    {
        var (cluster, log) = Started();
        for (int i = 1; i <= 5; i++) cluster.Fail(i);

        var result = cluster.ElectLeader();

        Assert.Null(result.LeaderId);
        Assert.Null(cluster.Leader);
        Assert.True(log.Contains("no available nodes"));
    }

    [Fact]
    public void Propose_WithoutAnyAvailableNode_ReturnsNoLeader_NotConsensus()
    {
        var (cluster, _) = Started();
        for (int i = 1; i <= 5; i++) cluster.Fail(i);

        var result = ProposeVersion(cluster, 1);

        Assert.False(result.ConsensusAchieved);
        Assert.Equal(ErrorCodes.NoLeader, result.ErrorCode);
    }

    [Fact]
    public void Deliver_NullMessage_ThrowsInvalidMessage_AndIsLogged()
    {
        var (cluster, log) = Started();

        var ex = Assert.Throws<ConsensusException>(() => cluster.Deliver(null));

        Assert.Equal(ErrorCodes.InvalidMessage, ex.ErrorCode);
        Assert.True(log.Contains("INVALID MESSAGE"));
    }

    [Fact]
    public void Deliver_UnknownRecipient_ThrowsInvalidMessage()
    {
        var (cluster, _) = Started();

        var ex = Assert.Throws<ConsensusException>(() =>
            cluster.Deliver(new NodeMessage(MessageType.Heartbeat, 1, 42, 1)));

        Assert.Equal(ErrorCodes.InvalidMessage, ex.ErrorCode);
    }

    [Fact]
    public void Node_UnknownSender_ReturnsErrorMessage_WithoutException()
    {
        var (cluster, log) = Started();

        var response = cluster.Deliver(new NodeMessage(MessageType.Heartbeat, 77, 2, 1));

        Assert.NotNull(response);
        Assert.Equal(MessageType.Error, response!.Type);
        Assert.True(log.Contains("INVALID MESSAGE from Node 77"));
    }

    [Fact]
    public void Node_ProposeWithoutPayload_ReturnsError()
    {
        var (cluster, _) = Started();

        var response = cluster.Deliver(new NodeMessage(MessageType.Propose, 5, 2, 1));

        Assert.Equal(MessageType.Error, response!.Type);
    }

    [Fact]
    public void Node_UnexpectedMessageType_ReturnsError()
    {
        var (cluster, _) = Started();

        var response = cluster.Deliver(new NodeMessage(MessageType.Vote, 5, 2, 1, Yes: true));

        Assert.Equal(MessageType.Error, response!.Type);
    }

    [Fact]
    public void Node_StaleLeaderAnnounce_IsRejected()
    {
        var (cluster, _) = Started();
        cluster.Fail(5);
        cluster.HeartbeatRound(); // term = 2

        var response = cluster.Deliver(new NodeMessage(MessageType.LeaderAnnounce, 5, 1, 1));

        Assert.Equal(MessageType.Error, response!.Type);
        Assert.Equal(4, cluster.GetNode(1).KnownLeaderId);
    }

    [Fact]
    public void Node_DuplicateCommit_IsIdempotent()
    {
        var (cluster, _) = Started();
        var result = ProposeVersion(cluster, 3);

        var again = cluster.Deliver(new NodeMessage(MessageType.Commit, 5, 1, 1, result.Proposal));

        Assert.Equal(MessageType.Ack, again!.Type);
        Assert.Equal(1, cluster.GetNode(1).CommittedLog.Count);
    }

    [Fact]
    public void ConcurrentProposals_AreSerialized_AndIndexesAreUnique()
    {
        var (cluster, _) = Started();

        var tasks = Enumerable.Range(1, 20)
            .Select(v => Task.Run(() => ProposeVersion(cluster, v)))
            .ToArray();
        Task.WaitAll(tasks);

        var indexes = cluster.Decisions.Select(d => d.Proposal.Index).ToList();
        Assert.Equal(indexes.Count, indexes.Distinct().Count());
        Assert.Equal(1, cluster.LeaderRoleCount);
        // Все узлы согласованы по последнему принятому индексу.
        Assert.Equal(1, cluster.Nodes.Select(n => n.LastCommittedIndex).Distinct().Count());
    }

    // ---- журнал ---------------------------------------------------------------------------------

    [Fact]
    public void Log_ContainsTimestampedEntries_InExpectedOrder()
    {
        var (cluster, log) = Started();
        cluster.Fail(5);
        cluster.HeartbeatRound();
        ProposeVersion(cluster, 12);

        int election = log.IndexOf("Election started");
        int timeout = log.IndexOf("HEARTBEAT TIMEOUT");
        int reelection = log.IndexOf("New election started");
        int propose = log.IndexOf("PROPOSE");
        int consensus = log.IndexOf("CONSENSUS ACHIEVED");

        Assert.True(election >= 0 && election < timeout);
        Assert.True(timeout < reelection);
        Assert.True(reelection < propose);
        Assert.True(propose < consensus);
        Assert.True(log.Lines.All(l => l.Length > 14 && l[0] == '[' && l[13] == ']'));
    }

    // ---- эксперименты части 6 ------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Experiments_ProduceExpectedResults(int number)
    {
        var result = ConsensusExperiments.Run(number);

        Assert.True(result.Passed);
        Assert.True(result.Journal.Count > 0);
    }

    [Fact]
    public void Experiment_UnknownNumber_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsensusExperiments.Run(9));
    }
}
