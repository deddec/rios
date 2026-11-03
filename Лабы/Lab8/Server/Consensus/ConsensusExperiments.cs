using FlowerShop.Shared.Consensus;

namespace FlowerShop.Server.Consensus;

/// <summary>
/// Часть 6: эксперименты с отказами. Каждый эксперимент создаёт новый кластер из 5 узлов
/// со своим журналом, выполняет сценарий и сравнивает результат с ожидаемым.
/// </summary>
public static class ConsensusExperiments
{
    public const int Count = 5;

    public static IReadOnlyList<ExperimentResultDto> RunAll(Action<string>? echo = null) =>
        Enumerable.Range(1, Count).Select(n => Run(n, echo)).ToList();

    public static ExperimentResultDto Run(int number, Action<string>? echo = null)
    {
        var log = new ConsensusLog();
        if (echo is not null) log.LineWritten += echo;
        var cluster = new ConsensusCluster(log);

        string title;
        bool expectAchieved = true;
        ProposalResult result;

        void Header(string text)
        {
            title = text;
            log.Write($"===== Эксперимент {number}: {text} =====");
        }

        title = string.Empty;
        switch (number)
        {
            case 1:
                Header("все узлы доступны (5/5)");
                cluster.Start();
                result = cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", "10");
                break;

            case 2:
                Header("отказ одного узла — Node 5 (4/5)");
                cluster.Start();
                cluster.Fail(5);
                cluster.HeartbeatRound();
                result = cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", "11");
                break;

            case 3:
                Header("отказ двух узлов — Node 4, Node 5 (3/5)");
                cluster.Start();
                cluster.Fail(4);
                cluster.Fail(5);
                cluster.HeartbeatRound();
                result = cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", "12");
                break;

            case 4:
                Header("отказ трёх узлов — Node 3, Node 4, Node 5 (2/5)");
                expectAchieved = false;
                cluster.Start();
                cluster.Fail(3);
                cluster.Fail(4);
                cluster.Fail(5);
                cluster.HeartbeatRound();
                result = cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", "13");
                break;

            case 5:
                Header("голоса разделились: сначала 1 узел против (YES=4), затем 3 узла против (YES=2)");
                cluster.Start();
                cluster.SetDissent(4, true);
                var first = cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", "20");
                cluster.SetDissent(3, true);
                cluster.SetDissent(5, true);
                var second = cluster.Propose(ProposalKind.ConfigurationVersion, "ConfigurationVersion", "21");
                // Итог эксперимента: первое предложение принято, второе — нет.
                expectAchieved = true;
                result = first.ConsensusAchieved && !second.ConsensusAchieved
                    ? first
                    : second;
                log.Write(first.ConsensusAchieved && !second.ConsensusAchieved
                    ? "Итог: предложение 20 принято (YES=4), предложение 21 отклонено (YES=2 < 3)"
                    : "Итог: неожиданный результат");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(number), $"Эксперимента №{number} не существует (1–{Count})");
        }

        bool passed = result.ConsensusAchieved == expectAchieved;
        log.Write(passed
            ? $"Эксперимент {number}: ожидаемый результат получен"
            : $"Эксперимент {number}: РЕЗУЛЬТАТ НЕ СОВПАЛ С ОЖИДАЕМЫМ");

        return new ExperimentResultDto
        {
            Number = number,
            Title = title,
            AvailableNodes = cluster.AvailableCount,
            TotalNodes = cluster.TotalNodes,
            Expected = expectAchieved ? "CONSENSUS ACHIEVED" : "CONSENSUS NOT ACHIEVED",
            Actual = result.ConsensusAchieved ? "CONSENSUS ACHIEVED" : "CONSENSUS NOT ACHIEVED",
            Passed = passed,
            Journal = log.Lines.ToList()
        };
    }
}
