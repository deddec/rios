using FlowerShop.Server.Data;
using FlowerShop.Shared.Protocol;
using Microsoft.Data.SqlClient;

namespace FlowerShop.Server.Replication;

/// <summary>
/// ЛР №5: отчёт о согласованности данных Primary и Replica.
/// Consistent=true — копии совпадают по реплицируемым данным (букеты + заказы).
/// </summary>
public sealed record ConsistencyReport(bool Consistent, List<string> Differences)
{
    public static ConsistencyReport Ok() => new(true, new List<string>());
}

/// <summary>
/// ЛР №5: отчёт о состоянии экземпляров хранения (для операции GetInstanceState).
/// </summary>
public sealed record InstanceStateReport(
    string PrimaryState,
    string ReplicaState,
    string ActiveInstance,
    string PrimaryDatabase,
    string ReplicaDatabase,
    bool Consistent,
    List<string> Differences);

/// <summary>
/// ЛР №5: менеджер репликации и отказоустойчивости. Учебная модель
/// Primary → Replica → Failover → Recovery (промышленный кластер СУБД не создаётся).
/// </summary>
public sealed class ReplicationManager
{
    /// <summary>Серверный журнал (события репликации/failover/recovery пишутся в консоль).</summary>
    public Action<string, string>? Logger { get; set; }

    /// <summary>Основной экземпляр хранения (FlowerShop).</summary>
    public StorageInstance Primary { get; }

    /// <summary>Резервная копия (FlowerShopReplica).</summary>
    public StorageInstance Replica { get; }

    /// <summary>Роль экземпляра, обслуживающего запросы сейчас (Primary или Replica).</summary>
    public ReplicaRole ActiveRole { get; private set; } = ReplicaRole.Primary;

    /// <summary>Активный экземпляр (которым сейчас пользуется приложение).</summary>
    public StorageInstance Active => ActiveRole == ReplicaRole.Primary ? Primary : Replica;

    /// <summary>Резервный (неактивный) экземпляр.</summary>
    public StorageInstance Standby => ActiveRole == ReplicaRole.Primary ? Replica : Primary;

    /// <summary>Имя активного экземпляра ("Primary"/"Replica").</summary>
    public string ActiveName => Active.Name;

    public ReplicationManager(StorageInstance primary, StorageInstance replica)
    {
        Primary = primary ?? throw new ArgumentNullException(nameof(primary));
        Replica = replica ?? throw new ArgumentNullException(nameof(replica));
    }

    private void Log(RequestContext? context, string phase, string detail)
    {
        context?.Journal?.Invoke(phase, detail);
        Logger?.Invoke(phase, detail);
    }

    // ===== Требование 5 ч.1: определение состояния экземпляров ===========

    /// <summary>Активная проверка доступности обоих экземпляров (ping) и обновление состояния.</summary>
    public void RefreshStates()
    {
        Primary.CheckAvailability(out _);
        Replica.CheckAvailability(out _);
    }

    /// <summary>Текущее состояние Primary ("AVAILABLE"/"UNAVAILABLE").</summary>
    public string PrimaryState => Primary.State.ToDisplayString();

    /// <summary>Текущее состояние Replica ("AVAILABLE"/"UNAVAILABLE").</summary>
    public string ReplicaState => Replica.State.ToDisplayString();

    // ===== Требования 2–3 ч.3: обнаружение отказа и Failover ============

    /// <summary>
    /// Получить активный экземпляр для обслуживания запроса. Если активный экземпляр
    /// (в штатном режиме — Primary) недоступен, выполняется Failover на Replica.
    /// Если недоступны оба экземпляра — бросает <see cref="OperationException"/>
    /// с кодом <see cref="ErrorCodes.AllInstancesUnavailable"/> (Требование 5).
    /// </summary>
    public StorageInstance GetActive(RequestContext? context = null)
    {
        // Активная проверка: состояние могло измениться (реальный или смоделированный отказ).
        Active.CheckAvailability(out string activeError);

        if (Active.IsAvailable)
        {
            return Active;
        }

        // Активный экземпляр отказён → пытаемся переключиться на резервный.
        Log(context, "FAILOVER",
            $"Обнаружен отказ {Active.Name} ({activeError}) — попытка переключения на {Standby.Name}");

        if (Failover(context))
        {
            return Active;
        }

        // Резервный тоже недоступен — переключаться некуда.
        throw new OperationException(ErrorCodes.AllInstancesUnavailable,
            $"Недоступны оба экземпляры: {Primary.Name}={PrimaryState}, {Replica.Name}={ReplicaState}. " +
            "Выполнить операцию невозможно.");
    }

    /// <summary>
    /// Failover — переключение приложения с отказавшего экземпляра на резервный.
    /// Возвращает true, если переключение выполнено (активным стал Replica).
    /// </summary>
    public bool Failover(RequestContext? context = null)
    {
        Standby.CheckAvailability(out string standbyError);

        if (!Standby.IsAvailable)
        {
            Log(context, "FAILOVER_FAIL",
                $"Невозможно выполнить Failover: {Standby.Name} тоже недоступен ({standbyError})");
            return false;
        }

        ReplicaRole previous = ActiveRole;
        ActiveRole = ReplicaRole.Replica;
        Log(context, "FAILOVER",
            $"Failover выполнен: {previous.ToDisplayString()} → Replica. " +
            $"Запросы обслуживаются через {Replica.Name} ({Replica.DatabaseName}).");
        return true;
    }

    // ===== Требование 6 ч.1 и ч.2: репликация изменений ==================

    /// <summary>
    /// Репликация: передача изменений из активного экземпляра в резервный.
    /// Вызывается ПОСЛЕ успешного изменения данных в активном экземпляре.
    /// Если резервный недоступен — репликация откладывается (изменения копятся
    /// в активном экземпляре и синхронизируются при Recovery).
    /// </summary>
    public bool ReplicateAfterChange(RequestContext? context = null)
    {
        var source = Active;
        var target = Standby;

        Log(context, "REPLICATION",
            $"Replication → START: передача изменений {source.Name} → {target.Name}");

        target.CheckAvailability(out string targetError);
        if (!target.IsAvailable)
        {
            Log(context, "REPLICATION_SKIPPED",
                $"Replication отложена: {target.Name} недоступен ({targetError}). " +
                $"Изменения сохранены в {source.Name} и будут синхронизированы после восстановления.");
            return false;
        }

        try
        {
            CopyReplicatedData(source, target);
            Log(context, "REPLICATION",
                $"Replication → SUCCESS: {source.Name} = {target.Name} (данные согласованы)");
            return true;
        }
        catch (Exception ex)
        {
            Log(context, "REPLICATION_FAIL",
                $"Ошибка репликации {source.Name} → {target.Name}: {ex.Message}. " +
                $"Изменения остались в {source.Name}.");
            return false;
        }
    }

    // ===== Требования 1–2 ч.4: Recovery и синхронизация ==================

    /// <summary>
    /// Восстановление Primary (Recovery). Экземпляр снова помечается доступным,
    /// затем актуальные данные переносятся из активного экземпляра (во время
    /// отказа — Replica) в Primary, и активным вновь становится Primary.
    /// </summary>
    public bool Recover(RequestContext? context = null)
    {
        Log(context, "RECOVERY", $"Recovery → START: восстановление {Primary.Name}");

        Primary.SetAvailable(true);
        Primary.CheckAvailability(out string primaryError);
        if (!Primary.IsAvailable)
        {
            Log(context, "RECOVERY_FAIL", $"Не удалось восстановить {Primary.Name}: {primaryError}");
            return false;
        }

        // Актуальное состояние находится в активном экземпляре (во время отказа — Replica).
        var authoritative = Active;
        Log(context, "SYNCHRONIZATION",
            $"Синхронизация после восстановления: {authoritative.Name} → {Primary.Name}");

        try
        {
            CopyReplicatedData(authoritative, Primary);
        }
        catch (Exception ex)
        {
            Log(context, "SYNCHRONIZATION_FAIL",
                $"Ошибка синхронизации {authoritative.Name} → {Primary.Name}: {ex.Message}");
            return false;
        }

        // Возвращаем активным Primary (штатный режим).
        ActiveRole = ReplicaRole.Primary;
        Log(context, "RECOVERY",
            $"Recovery → SUCCESS: {Primary.Name} восстановлен и синхронизирован. Активный экземпляр снова {Primary.Name}.");

        ConsistencyReport report = CheckConsistency();
        Log(context, "RECOVERY",
            report.Consistent
                ? "Проверка после восстановления: Primary = Replica (согласованы)"
                : $"Проверка после восстановления: расхождения остались: {string.Join("; ", report.Differences)}");
        return report.Consistent;
    }

    /// <summary>Синхронизация: приведение резервного экземпляра к состоянию активного.</summary>
    public bool Synchronize(RequestContext? context = null)
    {
        var source = Active;
        var target = Standby;

        Log(context, "SYNCHRONIZATION", $"Synchronize → START: {source.Name} → {target.Name}");

        target.CheckAvailability(out string targetError);
        if (!target.IsAvailable)
        {
            Log(context, "SYNCHRONIZATION_FAIL",
                $"Невозможно синхронизировать: {target.Name} недоступен ({targetError})");
            return false;
        }

        try
        {
            CopyReplicatedData(source, target);
        }
        catch (Exception ex)
        {
            Log(context, "SYNCHRONIZATION_FAIL",
                $"Ошибка синхронизации {source.Name} → {target.Name}: {ex.Message}");
            return false;
        }

        ConsistencyReport report = CheckConsistency();
        Log(context, "SYNCHRONIZATION",
            report.Consistent
                ? $"Synchronize → SUCCESS: {source.Name} = {target.Name}"
                : $"Synchronize завершена с расхождениями: {string.Join("; ", report.Differences)}");
        return report.Consistent;
    }

    // ===== Требование 7 ч.1 и Требование 2 ч.4: проверка согласованности ==

    /// <summary>
    /// Проверить согласованность Primary и Replica по реплицируемым данным:
    /// количество доступных букетов (общий ресурс) и набор заказов.
    /// </summary>
    public ConsistencyReport CheckConsistency()
    {
        var differences = new List<string>();

        Primary.CheckAvailability(out _);
        Replica.CheckAvailability(out _);
        if (!Primary.IsAvailable || !Replica.IsAvailable)
        {
            differences.Add($"Сравнение невозможно: Primary={PrimaryState}, Replica={ReplicaState}");
            return new ConsistencyReport(false, differences);
        }

        // Сравнение общего ресурса: AvailableCount по каждому букету.
        var primaryBouquets = Primary.Repository.ListBouquets().ToDictionary(b => b.Id, b => b.AvailableCount);
        var replicaBouquets = Replica.Repository.ListBouquets().ToDictionary(b => b.Id, b => b.AvailableCount);

        foreach (var (id, count) in primaryBouquets)
        {
            if (!replicaBouquets.TryGetValue(id, out int replicaCount))
                differences.Add($"Букет #{id} отсутствует в {Replica.Name}");
            else if (replicaCount != count)
                differences.Add($"Букет #{id}: Primary={count}, Replica={replicaCount}");
        }
        foreach (int id in replicaBouquets.Keys)
        {
            if (!primaryBouquets.ContainsKey(id))
                differences.Add($"Букет #{id} отсутствует в {Primary.Name}");
        }

        // Сравнение заказов (по идентификаторам).
        var primaryOrders = Primary.Repository.ListOrders().Select(o => o.Id).ToHashSet();
        var replicaOrders = Replica.Repository.ListOrders().Select(o => o.Id).ToHashSet();
        foreach (int id in primaryOrders.Where(id => !replicaOrders.Contains(id)))
            differences.Add($"Заказ #{id} есть в {Primary.Name}, отсутствует в {Replica.Name}");
        foreach (int id in replicaOrders.Where(id => !primaryOrders.Contains(id)))
            differences.Add($"Заказ #{id} есть в {Replica.Name}, отсутствует в {Primary.Name}");

        return new ConsistencyReport(differences.Count == 0, differences);
    }

    /// <summary>Отчёт о состоянии экземпляров для клиента (GetInstanceState).</summary>
    public InstanceStateReport GetStateReport()
    {
        ConsistencyReport report = CheckConsistency();
        return new InstanceStateReport(
            PrimaryState, ReplicaState, ActiveName,
            Primary.DatabaseName, Replica.DatabaseName,
            report.Consistent, report.Differences);
    }

    // ===== Моделирование отказа/восстановления (Требование 2 ч.3) ========

    /// <summary>Моделирование отказа или восстановления экземпляра из клиента/теста.</summary>
    public void SetInstanceState(ReplicaRole role, bool available)
    {
        StorageInstance instance = role == ReplicaRole.Primary ? Primary : Replica;
        instance.SetAvailable(available);
        Logger?.Invoke("STATE",
            $"{instance.Name} → {(available ? "AVAILABLE" : "UNAVAILABLE")} (смоделировано клиентом/тестом)");
    }

    // ===== Передача изменений (учебная модель репликации) =================

    /// <summary>
    /// Привести реплицируемые таблицы (dbo.Bouquets и dbo.Orders) экземпляра
    /// <paramref name="target"/> в соответствие с экземпляром <paramref name="source"/>.
    /// Это и есть «передача изменений» в учебной модели: переносится результирующее
    /// состояние реплицируемых данных. Справочники (Customers/Florists/FlowerItems)
    /// одинаковы в обеих копиях с момента начальной синхронизации и не реплицируются.
    /// Идентификаторы строк сохраняются (IDENTITY_INSERT), чтобы заказы и букеты
    /// совпадали по Id. Операция выполняется в транзакции (атомарность репликации).
    /// </summary>
    private static void CopyReplicatedData(StorageInstance source, StorageInstance target)
    {
        var bouquets = source.Repository.ListBouquets();
        var orders = source.Repository.ListOrders();

        using SqlConnection conn = target.Database.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            // --- Букеты (общий ресурс AvailableCount): обновить/вставить по Id ---
            foreach (var b in bouquets)
            {
                using var cmd = new SqlCommand(@"
                    IF EXISTS (SELECT 1 FROM dbo.Bouquets WHERE Id = @id)
                        UPDATE dbo.Bouquets SET Name = @name, AvailableCount = @count WHERE Id = @id;
                    ELSE
                    BEGIN
                        SET IDENTITY_INSERT dbo.Bouquets ON;
                        INSERT INTO dbo.Bouquets (Id, Name, AvailableCount) VALUES (@id, @name, @count);
                        SET IDENTITY_INSERT dbo.Bouquets OFF;
                    END", conn, tx);
                cmd.Parameters.AddWithValue("@id", b.Id);
                cmd.Parameters.AddWithValue("@name", b.Name);
                cmd.Parameters.AddWithValue("@count", b.AvailableCount);
                cmd.ExecuteNonQuery();
            }

            // --- Заказы: вставить отсутствующие, сохраняя Id ---
            foreach (var o in orders)
            {
                using var cmd = new SqlCommand(@"
                    IF NOT EXISTS (SELECT 1 FROM dbo.Orders WHERE Id = @id)
                    BEGIN
                        SET IDENTITY_INSERT dbo.Orders ON;
                        INSERT INTO dbo.Orders (Id, CustomerId, BouquetId, DeliveryDate, Status, FloristId, DeliveryAddress)
                        VALUES (@id, @cust, @bouq, @date, @status, @florist, @addr);
                        SET IDENTITY_INSERT dbo.Orders OFF;
                    END", conn, tx);
                cmd.Parameters.AddWithValue("@id", o.Id);
                cmd.Parameters.AddWithValue("@cust", o.CustomerId);
                cmd.Parameters.AddWithValue("@bouq", o.BouquetId);
                cmd.Parameters.AddWithValue("@date", o.DeliveryDate);
                cmd.Parameters.AddWithValue("@status", o.Status);
                cmd.Parameters.AddWithValue("@florist", (object?)o.FloristId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@addr", o.DeliveryAddress);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
