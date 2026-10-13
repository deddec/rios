namespace FlowerShop.Server.Replication;

/// <summary>
/// ЛР №5, Требование 5 части 1: роль экземпляра хранения данных.
/// Primary — основной экземпляр, с которым приложение работает в штатном режиме.
/// Replica — резервная копия, используемая при отказе Primary (Failover).
/// </summary>
public enum ReplicaRole
{
    /// <summary>Основной экземпляр данных (основные изменения выполняются здесь).</summary>
    Primary,

    /// <summary>Резервная копия данных (получает изменения из Primary).</summary>
    Replica
}

/// <summary>
/// ЛР №5, Требование 5 части 1: состояние экземпляра хранения данных.
/// Минимально необходимы AVAILABLE и UNAVAILABLE.
/// </summary>
public enum InstanceState
{
    /// <summary>Экземпляр доступен и готов обрабатывать операции.</summary>
    Available,

    /// <summary>Экземпляр недоступен (смоделирован или реальный отказ).</summary>
    Unavailable
}

/// <summary>Строковое представление состояния экземпляра для журнала и ответов клиенту.</summary>
public static class InstanceStateExtensions
{
    public static string ToDisplayString(this InstanceState state) =>
        state == InstanceState.Available ? "AVAILABLE" : "UNAVAILABLE";

    public static string ToDisplayString(this ReplicaRole role) =>
        role == ReplicaRole.Primary ? "Primary" : "Replica";
}
