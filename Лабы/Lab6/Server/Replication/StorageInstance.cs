using FlowerShop.Server.Data;

namespace FlowerShop.Server.Replication;

/// <summary>
/// ЛР №5: один экземпляр хранения данных (Primary или Replica).
///
/// Обертывает <see cref="Database"/> (соединение с конкретной базой MSSQL) и
/// <see cref="OrderRepository"/> (доступ к данным предметной области). Хранит
/// текущее состояние экземпляра (<see cref="InstanceState"/>) и позволяет
/// СМОДЕЛИРОВАТЬ отказ (Требование 2 части 3): при переводе в UNAVAILABLE
/// underlying-соединение блокируется, и любая операция с этим экземпляром
/// завершается ошибкой — приложение обязано это обнаружить и переключиться
/// на резервную копию.
///
/// Две независимые копии данных — это два отдельных <see cref="StorageInstance"/>
/// с разными базами (FlowerShop и FlowerShopReplica). Между ними реализуется
/// передача изменений (см. <see cref="ReplicationManager"/>).
/// </summary>
public sealed class StorageInstance
{
    /// <summary>Роль экземпляра: Primary (основной) или Replica (резервная копия).</summary>
    public ReplicaRole Role { get; }

    /// <summary>Отображаемое имя экземпляра: "Primary" или "Replica".</summary>
    public string Name => Role.ToDisplayString();

    /// <summary>Соединение с базой данного экземпляра (FlowerShop / FlowerShopReplica).</summary>
    public Database Database { get; }

    /// <summary>Репозиторий доступа к данным этого экземпляра.</summary>
    public OrderRepository Repository { get; }

    /// <summary>Текущее состояние экземпляра: AVAILABLE или UNAVAILABLE.</summary>
    public InstanceState State { get; private set; } = InstanceState.Available;

    /// <summary>true — экземпляр доступен (по состоянию; уточняется проверкой TryPing).</summary>
    public bool IsAvailable => State == InstanceState.Available;

    public StorageInstance(ReplicaRole role, Database database)
    {
        Role = role;
        Database = database ?? throw new ArgumentNullException(nameof(database));
        Repository = new OrderRepository(database);
    }

    /// <summary>Имя базы данного экземпляра (для журнала и ответов клиенту).</summary>
    public string DatabaseName => Database.DatabaseName;

    /// <summary>
    /// Моделирование отказа/восстановления экземпляра (Требование 2 части 3).
    /// При <paramref name="available"/>=false underlying-соединение блокируется
    /// (<see cref="Database.ForceUnavailable"/>), поэтому любая реальная операция
    /// с этим экземпляром бросает <see cref="OperationException"/> — это и есть
    /// «искусственно сделать Primary недоступным».
    /// </summary>
    public void SetAvailable(bool available)
    {
        State = available ? InstanceState.Available : InstanceState.Unavailable;
        Database.ForceUnavailable = !available;
    }

    /// <summary>
    /// Активная проверка доступности экземпляра (ping). Обновляет <see cref="State"/>.
    /// Используется для определения состояния экземпляра и обнаружения реального отказа.
    /// </summary>
    public bool CheckAvailability(out string error)
    {
        bool ok = Database.TryPing(out error);
        State = ok ? InstanceState.Available : InstanceState.Unavailable;
        return ok;
    }
}
