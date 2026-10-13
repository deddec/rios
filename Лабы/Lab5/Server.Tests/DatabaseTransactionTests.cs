using System.Text.Json;
using FlowerShop.Server;
using FlowerShop.Server.Data;
using FlowerShop.Shared.Protocol;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// Fact, который пропускает тест, если MSSQL недоступна (docker-контейнер не запущен).
/// Позволяет запускать полный набор тестов и на машине без БД.
/// </summary>
public sealed class DbFact : FactAttribute
{
    public DbFact()
    {
        if (!DbTestHelper.IsAvailable)
        {
            Skip = "MSSQL недоступна — запустите контейнер mssql/server:2022-latest (localhost:1433): " + DbTestHelper.LastInitError;
        }
    }
}

/// <summary>
/// ЛР №4: вспомогательные средства для тестов с реальной MSSQL.
/// </summary>
public static class DbTestHelper
{
    private static readonly (Database? Db, string? Error) Connection = ConnectCore();

    /// <summary>БД доступна (подключение установлено).</summary>
    public static bool IsAvailable => Connection.Db is not null;

    /// <summary>Последняя ошибка инициализации подключения (для диагностики пропуска тестов).</summary>
    public static string LastInitError => Connection.Error ?? "неизвестная ошибка";

    /// <summary>Подключённая БД (только когда <see cref="IsAvailable"/>).</summary>
    public static Database Db => Connection.Db
        ?? throw new InvalidOperationException("MSSQL недоступна: " + Connection.Error);

    private static (Database?, string?) ConnectCore()
    {
        try
        {
            var db = new Database();
            // СНАЧАЛА создать базу (EnsureDatabaseExists подключается к master),
            // иначе TryPing к ещё не созданной FlowerShop завершится ошибкой входа.
            db.EnsureDatabaseExists();
            if (!db.TryPing(out string error)) return (null, error);
            EnsureSchema(db);
            return (db, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Создать схему (идемпотентно); schema.sql лежит рядом с исходниками Server.</summary>
    private static void EnsureSchema(Database db)
    {
        db.EnsureDatabaseExists();
        string schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
        if (!File.Exists(schemaPath))
        {
            schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Server", "Data", "schema.sql"));
        }
        db.EnsureSchema(schemaPath);
    }

    /// <summary>Создать тестовый букет с заданным количеством и вернуть его Id.</summary>
    public static int CreateTestBouquet(int availableCount, string tag)
    {
        using SqlConnection conn = Db.Open();
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.Bouquets (Name, AvailableCount) OUTPUT INSERTED.Id VALUES (@name, @count)", conn);
        cmd.Parameters.AddWithValue("@name", $"Тест-{tag}-{Guid.NewGuid():N}"[..40]);
        cmd.Parameters.AddWithValue("@count", availableCount);
        return (int)cmd.ExecuteScalar()!;
    }

    /// <summary>Удалить тестовый букет со всеми связанными заказами.</summary>
    public static void DeleteTestBouquet(int bouquetId)
    {
        using SqlConnection conn = Db.Open();
        using var cmd = new SqlCommand(@"
            DELETE FROM dbo.Deliveries WHERE OrderId IN (SELECT Id FROM dbo.Orders WHERE BouquetId = @b);
            DELETE FROM dbo.OrderEvents WHERE OrderId IN (SELECT Id FROM dbo.Orders WHERE BouquetId = @b);
            DELETE FROM dbo.Orders WHERE BouquetId = @b;
            DELETE FROM dbo.Bouquets WHERE Id = @b;", conn);
        cmd.Parameters.AddWithValue("@b", bouquetId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сколько заказов оформлено на данный букет.</summary>
    public static int CountOrders(int bouquetId)
    {
        using SqlConnection conn = Db.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Orders WHERE BouquetId = @b", conn);
        cmd.Parameters.AddWithValue("@b", bouquetId);
        return (int)cmd.ExecuteScalar()!;
    }
}

/// <summary>
/// ЛР №4, Требование 7: тесты составной операции с транзакцией (COMMIT/ROLLBACK),
/// операций чтения/изменения через БД и операции с общим ресурсом под lock.
/// </summary>
public class DatabaseTransactionTests
{
    private static OrderRepository CreateRepository() => new(DbTestHelper.Db);

    // ----- Тесты, не требующие БД: обработка ошибки подключения ----------

    [Fact]
    public void TryPing_ReturnsWellDefinedResult_WithoutException()
    {
        // Даже если БД доступна, TryPing обязан вернуть осмысленный результат без исключений.
        var db = new Database();
        Assert.True(db.TryPing(out string error) || !string.IsNullOrEmpty(error));
    }

    // ----- Тесты составной операции (Требование 9) -----------------------

    [DbFact]
    public void PlaceOrderAndReserve_WithTransaction_CommitsBothOperations()
    {
        var repo = CreateRepository();
        int bouquetId = DbTestHelper.CreateTestBouquet(2, "commit");
        try
        {
            PlaceOrderOutcome outcome = repo.PlaceOrderAndReserve(
                customerId: 1, bouquetId, "ул. Коммитная, 1", DateTime.Today.AddDays(2),
                useTransaction: true, simulatedError: false);

            // COMMIT: заказ сохранён, ресурс уменьшен
            Assert.True(repo.Exists(outcome.OrderId), "После COMMIT заказ должен существовать в БД");
            Assert.Equal(2, outcome.AvailableBefore);
            Assert.Equal(1, outcome.AvailableAfter);
            Assert.Equal(1, repo.GetAvailableCount(bouquetId));
            Assert.Equal(1, DbTestHelper.CountOrders(bouquetId));
        }
        finally
        {
            DbTestHelper.DeleteTestBouquet(bouquetId);
        }
    }

    [DbFact]
    public void PlaceOrderAndReserve_WithSimulatedError_TransactionRollsBack_NoPartialChanges()
    {
        var repo = CreateRepository();
        int bouquetId = DbTestHelper.CreateTestBouquet(2, "rollback");
        try
        {
            // Operation 1 → SUCCESS, затем ошибка → ROLLBACK
            var ex = Assert.Throws<OperationException>(() => repo.PlaceOrderAndReserve(
                customerId: 1, bouquetId, "ул. Роллбэкова, 5", DateTime.Today.AddDays(2),
                useTransaction: true, simulatedError: true));

            Assert.Equal(ErrorCodes.TransactionError, ex.ErrorCode);
            // ROLLBACK: ни заказа, ни изменения ресурса
            Assert.Equal(0, DbTestHelper.CountOrders(bouquetId));
            Assert.Equal(2, repo.GetAvailableCount(bouquetId));
        }
        finally
        {
            DbTestHelper.DeleteTestBouquet(bouquetId);
        }
    }

    [DbFact]
    public void PlaceOrderAndReserve_WithoutTransaction_SimulatedError_LeavesPartialOrder()
    {
        var repo = CreateRepository();
        int bouquetId = DbTestHelper.CreateTestBouquet(2, "partial");
        try
        {
            // Без транзакции: INSERT заказа автокоммитится ДО ошибки — частичное изменение остаётся.
            var ex = Assert.Throws<OperationException>(() => repo.PlaceOrderAndReserve(
                customerId: 1, bouquetId, "ул. Частичная, 3", DateTime.Today.AddDays(2),
                useTransaction: false, simulatedError: true));

            Assert.Equal(ErrorCodes.TransactionError, ex.ErrorCode);
            // Частичное изменение: заказ в БД остался, ресурс не уменьшен (сравнение с ROLLBACK)
            Assert.Equal(1, DbTestHelper.CountOrders(bouquetId));
            Assert.Equal(2, repo.GetAvailableCount(bouquetId));
        }
        finally
        {
            DbTestHelper.DeleteTestBouquet(bouquetId);
        }
    }

    [DbFact]
    public void PlaceOrderAndReserve_UnknownCustomer_FailsWithoutChanges()
    {
        var repo = CreateRepository();
        int bouquetId = DbTestHelper.CreateTestBouquet(2, "unknown");
        try
        {
            // Нарушение внешнего ключа: несуществующий клиент — ошибка БД без частичных изменений.
            Assert.Throws<OperationException>(() => repo.PlaceOrderAndReserve(
                customerId: -1, bouquetId, "ул. Ошибочная, 13", DateTime.Today.AddDays(2),
                useTransaction: true, simulatedError: false));

            Assert.Equal(0, DbTestHelper.CountOrders(bouquetId));
            Assert.Equal(2, repo.GetAvailableCount(bouquetId));
        }
        finally
        {
            DbTestHelper.DeleteTestBouquet(bouquetId);
        }
    }

    // ----- Операции с общим ресурсом через БД (Требования 5, 6) ----------

    [DbFact]
    public async Task ReserveBouquetInDb_TwoConcurrentRequests_ExactlyOneSucceeds()
    {
        var logic = new BusinessLogic(sync: SyncMechanism.Lock, repository: CreateRepository());

        int bouquetId = DbTestHelper.CreateTestBouquet(1, "race");
        try
        {
            string payload = JsonSerializer.Serialize(new { bouquetId, criticalDelayMs = 500 });

            Task<Response> t1 = ExecuteReserveAsync(logic, payload);
            await Task.Delay(100);   // второй клиент стартует ПОКА первый внутри критической секции
            Task<Response> t2 = ExecuteReserveAsync(logic, payload);
            Response[] results = await Task.WhenAll(t1, t2);

            // При одном свободном букете SUCCESS получает ровно один клиент (Требование 6)
            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Equal(1, results.Count(r => !r.Success && r.ErrorCode == ErrorCodes.OutOfStock));
            Assert.Equal(0, CreateRepository().GetAvailableCount(bouquetId));
        }
        finally
        {
            DbTestHelper.DeleteTestBouquet(bouquetId);
        }
    }

    [DbFact]
    public async Task ReserveBouquetInDb_NoStock_ReturnsOutOfStock()
    {
        var logic = new BusinessLogic(sync: SyncMechanism.Lock, repository: CreateRepository());

        int bouquetId = DbTestHelper.CreateTestBouquet(0, "empty");
        try
        {
            string payload = JsonSerializer.Serialize(new { bouquetId, criticalDelayMs = 0 });
            Response response = await ExecuteReserveAsync(logic, payload);

            Assert.False(response.Success);
            Assert.Equal(ErrorCodes.OutOfStock, response.ErrorCode);
        }
        finally
        {
            DbTestHelper.DeleteTestBouquet(bouquetId);
        }
    }

    [DbFact]
    public async Task SetAvailabilityAndStatus_InDb_ReadWriteOperations()
    {
        var logic = new BusinessLogic(sync: SyncMechanism.Lock, repository: CreateRepository());

        int bouquetId = DbTestHelper.CreateTestBouquet(5, "rw");
        try
        {
            // Изменение
            Response set = await logic.ExecuteAsync(RequestOf("SetBouquetAvailability",
                new { bouquetId, count = 3 }));
            Assert.True(set.Success);
            Assert.Equal(3, CreateRepository().GetAvailableCount(bouquetId));

            // Чтение
            Response status = await logic.ExecuteAsync(RequestOf("GetBouquetStatus", new { bouquetId }));
            Assert.True(status.Success);
            Assert.NotNull(status.Data);
            Assert.Equal(3, status.Data!.Value.GetProperty("availableCount").GetInt32());
        }
        finally
        {
            DbTestHelper.DeleteTestBouquet(bouquetId);
        }
    }

    [DbFact]
    public async Task CreateOrderInDb_UnknownBouquet_ReturnsNotFound()
    {
        var logic = new BusinessLogic(sync: SyncMechanism.Lock, repository: CreateRepository());

        var ex = await Assert.ThrowsAsync<OperationException>(() => logic.ExecuteAsync(RequestOf("CreateOrder",
            new { customerId = 1, bouquetId = -1, deliveryAddress = "тест", deliveryDate = "2026-12-31" })));

        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
    }

    // ----- Операции с БД без подключённой БД ------------------------------

    [Fact]
    public async Task DbOperation_WithoutRepository_ReturnsDatabaseError()
    {
        var logic = new BusinessLogic(sync: SyncMechanism.Lock);   // БД не подключена

        var ex = await Assert.ThrowsAsync<OperationException>(() =>
            logic.ExecuteAsync(new Request { Operation = "ListOrders" }));

        Assert.Equal(ErrorCodes.DatabaseError, ex.ErrorCode);
    }

    [Fact]
    public async Task PlaceOrderAndReserve_WithoutRepository_ReturnsDatabaseError()
    {
        var logic = new BusinessLogic(sync: SyncMechanism.Lock);

        var ex = await Assert.ThrowsAsync<OperationException>(() => logic.ExecuteAsync(
            RequestOf("PlaceOrderAndReserve",
                new { customerId = 1, bouquetId = 1, deliveryAddress = "тест", deliveryDate = "2026-12-31" })));

        Assert.Equal(ErrorCodes.DatabaseError, ex.ErrorCode);
    }

    private static Request RequestOf(string operation, object payload) => new()
    {
        Operation = operation,
        Payload = JsonSerializer.SerializeToElement(payload)
    };

    private static async Task<Response> ExecuteReserveAsync(BusinessLogic logic, string payloadJson)
    {
        var request = new Request
        {
            Operation = "ReserveBouquet",
            Payload = JsonDocument.Parse(payloadJson).RootElement
        };
        // Как на реальном сервере: через RequestHandler, который превращает
        // OperationException (например, OUT_OF_STOCK) в Response.Fail.
        return await new RequestHandler(logic).HandleAsync(JsonSerializer.Serialize(request, Json.Options));
    }
}
