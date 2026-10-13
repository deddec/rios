using System.Text.Json;
using FlowerShop.Server;
using FlowerShop.Server.Data;
using FlowerShop.Server.Replication;
using FlowerShop.Shared.Protocol;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// ЛР №5: вспомогательные средства для тестов репликации на реальной MSSQL.
/// Для изоляции от других тестов используются ОТДЕЛЬНЫЕ базы данных
/// (FlowerShop_ReplTest_Primary и FlowerShop_ReplTest_Replica), создаваемые
/// с той же схемой, что и основные Primary/Replica.
/// </summary>
public static class ReplicationTestHelper
{
    public const string PrimaryDbName = "FlowerShop_ReplTest_Primary";
    public const string ReplicaDbName = "FlowerShop_ReplTest_Replica";

    private static readonly (Database? Primary, string? Error) Connection = ConnectCore();

    /// <summary>MSSQL доступна (подключение установлено).</summary>
    public static bool IsAvailable => Connection.Primary is not null;

    private static (Database?, string?) ConnectCore()
    {
        try
        {
            var primary = new Database(PrimaryDbName);
            var replica = new Database(ReplicaDbName);
            primary.EnsureDatabaseExists();
            replica.EnsureDatabaseExists();

            if (!primary.TryPing(out string e1)) return (null, $"Primary: {e1}");
            if (!replica.TryPing(out string e2)) return (null, $"Replica: {e2}");

            string schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
            if (!File.Exists(schemaPath))
            {
                schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "Server", "Data", "schema.sql"));
            }
            primary.EnsureSchema(schemaPath);
            replica.EnsureSchema(schemaPath);
            return (primary, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Создать менеджер репликации на тестовых БД и привести обе копии к одинаковому состоянию.</summary>
    public static ReplicationManager BuildManager()
    {
        var primary = new StorageInstance(ReplicaRole.Primary, new Database(PrimaryDbName));
        var replica = new StorageInstance(ReplicaRole.Replica, new Database(ReplicaDbName));
        var manager = new ReplicationManager(primary, replica);
        ResetToIdenticalState(primary, replica);
        return manager;
    }

    /// <summary>
    /// Полностью очистить реплицируемые таблицы в обеих копиях и засеять одинаковые
    /// демо-данные с одними и теми же идентификаторами — гарантия начального
    /// совпадения Primary = Replica (Требование 4 ч.1).
    /// </summary>
    private static void ResetToIdenticalState(StorageInstance primary, StorageInstance replica)
    {
        foreach (var instance in new[] { primary, replica })
        {
            ClearAll(instance);
        }
        SeedBouquet(primary, 1, "Розовое облако", 3);
        SeedBouquet(primary, 2, "Весенний", 5);
        SeedBouquet(primary, 3, "Новогодний люкс", 1);
        SeedBouquet(replica, 1, "Розовое облако", 3);
        SeedBouquet(replica, 2, "Весенний", 5);
        SeedBouquet(replica, 3, "Новогодний люкс", 1);
    }

    private static void ClearAll(StorageInstance instance)
    {
        using SqlConnection conn = instance.Database.Open();
        using var cmd = new SqlCommand(@"
            DELETE FROM dbo.Deliveries;
            DELETE FROM dbo.OrderEvents;
            DELETE FROM dbo.Orders;
            DELETE FROM dbo.FlowerItems;
            DELETE FROM dbo.Bouquets;", conn);
        cmd.ExecuteNonQuery();
    }

    private static void SeedBouquet(StorageInstance instance, int id, string name, int count)
    {
        using SqlConnection conn = instance.Database.Open();
        using var cmd = new SqlCommand(@"
            SET IDENTITY_INSERT dbo.Bouquets ON;
            INSERT INTO dbo.Bouquets (Id, Name, AvailableCount) VALUES (@id, @name, @count);
            SET IDENTITY_INSERT dbo.Bouquets OFF;", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@count", count);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Fact, пропускающий тест, если MSSQL недоступна.</summary>
public sealed class ReplDbFact : FactAttribute
{
    public ReplDbFact()
    {
        if (!ReplicationTestHelper.IsAvailable)
        {
            Skip = "MSSQL недоступна — запустите контейнер mssql/server:2022-latest (localhost:1433)";
        }
    }
}

/// <summary>
/// ЛР №5, Требование 4 ч.4: unit-тесты репликации и отказоустойчивости.
/// Покрывают: начальное состояние Primary=Replica; репликацию изменения;
/// обнаружение отказа Primary; Failover на Replica; работу через Replica;
/// восстановление Primary; синхронизацию; согласованность после синхронизации.
/// </summary>
public class ReplicationTests
{
    private static Request RequestOf(string operation, object? payload = null) => new()
    {
        Operation = operation,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload)
    };

    // ----- Часть 1: начальное состояние Primary = Replica -----------------

    [ReplDbFact]
    public void InitialState_PrimaryEqualsReplica()
    {
        var manager = ReplicationTestHelper.BuildManager();

        Assert.Equal(InstanceState.Available, manager.Primary.State);
        Assert.Equal(InstanceState.Available, manager.Replica.State);
        Assert.True(manager.CheckConsistency().Consistent,
            "Начальное состояние Primary и Replica должно совпадать");
        Assert.Equal(ReplicaRole.Primary, manager.ActiveRole);
    }

    // ----- Часть 2: репликация изменения --------------------------------

    [ReplDbFact]
    public void Replication_AfterChangeInPrimary_CopiesMatchReplica()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        Response response = logic.Execute(RequestOf("SetBouquetAvailability", new { bouquetId = 1, count = 1 }));
        Assert.True(response.Success);

        ConsistencyReport report = manager.CheckConsistency();
        Assert.True(report.Consistent, "После изменения и репликации копии должны совпадать: " +
            string.Join("; ", report.Differences));
        Assert.Equal(1, manager.Replica.Repository.GetAvailableCount(1));
    }

    [ReplDbFact]
    public void Replication_ThreeSequentialChanges_AlwaysConsistent()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        foreach (int count in new[] { 4, 2, 0 })
        {
            Response response = logic.Execute(RequestOf("SetBouquetAvailability", new { bouquetId = 2, count }));
            Assert.True(response.Success);
            Assert.True(manager.CheckConsistency().Consistent,
                $"После установки count={count} копии должны совпадать");
        }
        Assert.Equal(0, manager.Replica.Repository.GetAvailableCount(2));
    }

    [ReplDbFact]
    public void PlaceOrderAndReserve_ReplicatesToReplica()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        Response response = logic.Execute(RequestOf("PlaceOrderAndReserve", new
        {
            customerId = 1,
            bouquetId = 1,
            deliveryAddress = "ул. Тестовая, 1",
            deliveryDate = "2026-12-01",
            useTransaction = true,
            simulatedError = false
        }));
        Assert.True(response.Success);

        Assert.Equal(2, manager.Primary.Repository.GetAvailableCount(1));
        Assert.Equal(2, manager.Replica.Repository.GetAvailableCount(1));
        Assert.True(manager.CheckConsistency().Consistent);
    }

    // ----- Часть 3: обнаружение отказа и Failover -----------------------

    [ReplDbFact]
    public void DetectFailure_PrimaryUnavailable_StateIsUnavailable()
    {
        var manager = ReplicationTestHelper.BuildManager();

        manager.SetInstanceState(ReplicaRole.Primary, false);

        Assert.Equal(InstanceState.Unavailable, manager.Primary.State);
        Assert.Equal("UNAVAILABLE", manager.PrimaryState);
        Assert.Equal(InstanceState.Available, manager.Replica.State);
    }

    [ReplDbFact]
    public void Failover_PrimaryDown_SwitchesToReplica()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        manager.SetInstanceState(ReplicaRole.Primary, false);

        StorageInstance active = logic.Replication!.GetActive();
        Assert.Equal(ReplicaRole.Replica, active.Role);
        Assert.Equal(ReplicaRole.Replica, manager.ActiveRole);
    }

    [ReplDbFact]
    public void RequestAfterFailover_IsServedByReplica()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        manager.SetInstanceState(ReplicaRole.Primary, false);

        Response response = logic.Execute(RequestOf("SetBouquetAvailability", new { bouquetId = 3, count = 0 }));
        Assert.True(response.Success, "Запрос должен быть успешно обработан через Replica");
        Assert.Equal(0, manager.Replica.Repository.GetAvailableCount(3));
    }

    [ReplDbFact]
    public void BothInstancesUnavailable_ReturnsError()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        manager.SetInstanceState(ReplicaRole.Primary, false);
        manager.SetInstanceState(ReplicaRole.Replica, false);

        var ex = Assert.Throws<OperationException>(() =>
            logic.Execute(RequestOf("SetBouquetAvailability", new { bouquetId = 1, count = 2 })));
        Assert.Equal(ErrorCodes.AllInstancesUnavailable, ex.ErrorCode);
    }

    // ----- Часть 4: восстановление и синхронизация ----------------------

    [ReplDbFact]
    public void Recover_PrimaryRestored_AndSynchronized()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        // Отказ Primary → Failover → изменение через Replica.
        manager.SetInstanceState(ReplicaRole.Primary, false);
        logic.Execute(RequestOf("SetBouquetAvailability", new { bouquetId = 1, count = 0 }));

        // Во время отказа Primary отстал (Replica=0, Primary=3).
        Assert.False(manager.CheckConsistency().Consistent, "Во время отказа копии расходятся");

        // Восстановление Primary и синхронизация.
        bool recovered = manager.Recover();
        Assert.True(recovered);
        Assert.Equal(ReplicaRole.Primary, manager.ActiveRole);
        Assert.True(manager.CheckConsistency().Consistent, "После Recovery Primary = Replica");
        Assert.Equal(0, manager.Primary.Repository.GetAvailableCount(1));
    }

    [ReplDbFact]
    public void Synchronize_BringsReplicaToActiveState()
    {
        var manager = ReplicationTestHelper.BuildManager();

        // Меняем Primary в обход репликации (имитация расхождения).
        manager.Primary.Repository.SetAvailableCount(2, 1);
        Assert.False(manager.CheckConsistency().Consistent);

        bool synced = manager.Synchronize();
        Assert.True(synced);
        Assert.True(manager.CheckConsistency().Consistent);
        Assert.Equal(1, manager.Replica.Repository.GetAvailableCount(2));
    }

    // ----- Операции протокола ЛР №5 через BusinessLogic -----------------

    [ReplDbFact]
    public void GetInstanceState_ReturnsBothStatesAndActive()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        Response response = logic.Execute(RequestOf("GetInstanceState"));
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("AVAILABLE", response.Data!.Value.GetProperty("primaryState").GetString());
        Assert.Equal("AVAILABLE", response.Data.Value.GetProperty("replicaState").GetString());
        Assert.Equal("Primary", response.Data.Value.GetProperty("activeInstance").GetString());
    }

    [ReplDbFact]
    public void CheckConsistency_ConsistentData_ReportsTrue()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        Response response = logic.Execute(RequestOf("CheckConsistency"));
        Assert.True(response.Success);
        Assert.True(response.Data!.Value.GetProperty("consistent").GetBoolean());
    }

    [ReplDbFact]
    public void SetInstanceState_InvalidInstance_ReturnsValidationError()
    {
        var manager = ReplicationTestHelper.BuildManager();
        var logic = new BusinessLogic(replication: manager);

        var ex = Assert.Throws<OperationException>(() =>
            logic.Execute(RequestOf("SetInstanceState", new { instance = "unknown", available = false })));
        Assert.Equal(ErrorCodes.ValidationError, ex.ErrorCode);
    }

    [Fact]
    public void ReplicationOperation_WithoutReplication_ReturnsDatabaseError()
    {
        // Без менеджера репликации операции ЛР №5 недоступны — понятная ошибка.
        var logic = new BusinessLogic();

        var ex = Assert.Throws<OperationException>(() => logic.Execute(RequestOf("GetInstanceState")));
        Assert.Equal(ErrorCodes.DatabaseError, ex.ErrorCode);
    }
}
