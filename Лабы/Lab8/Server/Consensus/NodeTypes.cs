namespace FlowerShop.Server.Consensus;

/// <summary>Состояние узла: доступен или отказал.</summary>
public enum NodeStatus
{
    Available,
    Failed
}

/// <summary>Роль узла в кластере: в любой момент не более одного доступного Leader.</summary>
public enum NodeRole
{
    Follower,
    Leader
}

/// <summary>Тип сообщения, которым обмениваются узлы.</summary>
public enum MessageType
{
    /// <summary>Проверка доступности узла (HEARTBEAT?).</summary>
    Heartbeat,

    /// <summary>Ответ на heartbeat (ALIVE).</summary>
    Alive,

    /// <summary>Объявление результата выборов: узел-отправитель — новый лидер.</summary>
    LeaderAnnounce,

    /// <summary>Предложение значения от лидера.</summary>
    Propose,

    /// <summary>Голос узла (YES / NO) в ответ на предложение.</summary>
    Vote,

    /// <summary>Фиксация решения, принятого большинством.</summary>
    Commit,

    /// <summary>Подтверждение получения сообщения.</summary>
    Ack,

    /// <summary>Сообщение об ошибке (например, некорректное сообщение).</summary>
    Error
}

/// <summary>Вид предлагаемого значения.</summary>
public enum ProposalKind
{
    /// <summary>ConfigurationVersion = N — версия конфигурации кластера.</summary>
    ConfigurationVersion,

    /// <summary>Смена статуса заказа (предметная область: вариант 14, OrderStatus).</summary>
    OrderStatus
}

/// <summary>
/// Предложение лидера: «значение Value для ключа Key под номером Index в эпохе Term».
/// Для <see cref="ProposalKind.ConfigurationVersion"/> Key = "ConfigurationVersion",
/// для <see cref="ProposalKind.OrderStatus"/> Key = Id заказа, Value = новый статус.
/// </summary>
public sealed record Proposal(long Index, int Term, ProposalKind Kind, string Key, string Value, int ProposerId)
{
    /// <summary>Короткое описание для журнала.</summary>
    public string Describe() => Kind switch
    {
        ProposalKind.ConfigurationVersion => $"Version={Value}",
        ProposalKind.OrderStatus => $"Order #{Key} -> {Value}",
        _ => $"{Key}={Value}"
    };
}

/// <summary>Сообщение между узлами.</summary>
public sealed record NodeMessage(
    MessageType Type,
    int FromId,
    int ToId,
    int Term,
    Proposal? Proposal = null,
    bool? Yes = null,
    string? Reason = null);

/// <summary>Исключение уровня кластера с кодом ошибки протокола.</summary>
public sealed class ConsensusException : Exception
{
    public string ErrorCode { get; }

    public ConsensusException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
