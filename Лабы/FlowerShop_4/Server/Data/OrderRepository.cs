using System.Data;
using Microsoft.Data.SqlClient;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server.Data;

/// <summary>
/// ЛР №4: репозиторий заказов для доступа к MSSQL.
/// Реализует CRUD-операции для заказов, букетов и резервирования.
/// Поддерживает транзакционные и нетранзакционные режимы выполнения.
/// </summary>
public sealed class OrderRepository
{
    private readonly Database _database;

    public OrderRepository(Database database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Получить все заказы из базы данных.
    /// </summary>
    public List<Order> ListOrders()
    {
        var orders = new List<Order>();

        using SqlConnection conn = _database.Open();
        string sql = @"
            SELECT Id, CustomerId, BouquetId, DeliveryDate, Status, FloristId, DeliveryAddress
            FROM dbo.Orders
            ORDER BY Id";

        using (var cmd = new SqlCommand(sql, conn))
        {
            using SqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                orders.Add(new Order
                {
                    Id = reader.GetInt32(0),
                    CustomerId = reader.GetInt32(1),
                    BouquetId = reader.GetInt32(2),
                    DeliveryDate = reader.GetDateTime(3),
                    Status = reader.GetString(4),
                    FloristId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    DeliveryAddress = reader.GetString(6)
                });
            }
        }

        return orders;
    }

    /// <summary>
    /// Получить заказ по Id.
    /// </summary>
    public Order? GetOrder(int orderId)
    {
        using SqlConnection conn = _database.Open();
        string sql = @"
            SELECT Id, CustomerId, BouquetId, DeliveryDate, Status, FloristId, DeliveryAddress
            FROM dbo.Orders
            WHERE Id = @orderId";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@orderId", orderId);
            using SqlDataReader reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new Order
                {
                    Id = reader.GetInt32(0),
                    CustomerId = reader.GetInt32(1),
                    BouquetId = reader.GetInt32(2),
                    DeliveryDate = reader.GetDateTime(3),
                    Status = reader.GetString(4),
                    FloristId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    DeliveryAddress = reader.GetString(6)
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Проверить, существует ли заказ с заданным Id.
    /// </summary>
    public bool Exists(int orderId)
    {
        using SqlConnection conn = _database.Open();
        string sql = "SELECT 1 FROM dbo.Orders WHERE Id = @orderId";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@orderId", orderId);
            return cmd.ExecuteScalar() != null;
        }
    }

    /// <summary>
    /// Получить все букеты из базы данных.
    /// </summary>
    public List<Bouquet> ListBouquets()
    {
        var bouquets = new List<Bouquet>();

        using SqlConnection conn = _database.Open();
        string sql = @"
            SELECT Id, Name, AvailableCount
            FROM dbo.Bouquets
            ORDER BY Id";

        using (var cmd = new SqlCommand(sql, conn))
        {
            using SqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int bouquetId = reader.GetInt32(0);
                var bouquet = new Bouquet
                {
                    Id = bouquetId,
                    Name = reader.GetString(1),
                    AvailableCount = reader.GetInt32(2)
                };

                // Загрузить цветы этого букета
                bouquet.Flowers = GetFlowerItems(conn, bouquetId);
                bouquets.Add(bouquet);
            }
        }

        return bouquets;
    }

    /// <summary>
    /// Получить количество доступных экземпляров букета.
    /// </summary>
    public int GetAvailableCount(int bouquetId)
    {
        using SqlConnection conn = _database.Open();
        string sql = "SELECT AvailableCount FROM dbo.Bouquets WHERE Id = @bouquetId";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
            object? result = cmd.ExecuteScalar();
            return result != null ? (int)result : 0;
        }
    }

    /// <summary>ЛР №4: получить один букет (с составом) по Id, либо null, если не найден.</summary>
    public Bouquet? GetBouquet(int bouquetId)
    {
        using SqlConnection conn = _database.Open();
        string sql = "SELECT Id, Name, AvailableCount FROM dbo.Bouquets WHERE Id = @bouquetId";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
            using SqlDataReader reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var bouquet = new Bouquet
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                AvailableCount = reader.GetInt32(2)
            };
            reader.Close();
            bouquet.Flowers = GetFlowerItems(conn, bouquetId);
            return bouquet;
        }
    }

    /// <summary>ЛР №4: существует ли клиент с указанным Id.</summary>
    public bool CustomerExists(int customerId)
    {
        using SqlConnection conn = _database.Open();
        using var cmd = new SqlCommand("SELECT 1 FROM dbo.Customers WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", customerId);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>ЛР №4: существует ли букет с указанным Id.</summary>
    public bool BouquetExists(int bouquetId)
    {
        using SqlConnection conn = _database.Open();
        using var cmd = new SqlCommand("SELECT 1 FROM dbo.Bouquets WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", bouquetId);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Создать новый заказ в базе данных.
    /// </summary>
    public int CreateOrder(int customerId, int bouquetId, string deliveryAddress, DateTime deliveryDate)
    {
        using SqlConnection conn = _database.Open();
        string sql = @"
            INSERT INTO dbo.Orders (CustomerId, BouquetId, DeliveryDate, Status, DeliveryAddress)
            OUTPUT INSERTED.Id
            VALUES (@customerId, @bouquetId, @deliveryDate, @status, @address)";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@customerId", customerId);
            cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
            cmd.Parameters.AddWithValue("@deliveryDate", deliveryDate);
            cmd.Parameters.AddWithValue("@status", OrderStatus.Created);
            cmd.Parameters.AddWithValue("@address", deliveryAddress);

            object? result = cmd.ExecuteScalar();
            return result != null ? (int)result : -1;
        }
    }

    /// <summary>
    /// Установить количество доступных экземпляров букета.
    /// </summary>
    public void SetAvailableCount(int bouquetId, int count)
    {
        using SqlConnection conn = _database.Open();
        string sql = "UPDATE dbo.Bouquets SET AvailableCount = @count WHERE Id = @bouquetId";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@count", Math.Max(0, count));
            cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Попытка зарезервировать один экземпляр букета.
    /// Возвращает true если успешно, false если букет недоступен.
    /// </summary>
    public bool TryReserveOne(int bouquetId)
    {
        using SqlConnection conn = _database.Open();

        // Проверить наличие и уменьшить в одной операции
        string sql = @"
            UPDATE dbo.Bouquets
            SET AvailableCount = AvailableCount - 1
            WHERE Id = @bouquetId AND AvailableCount > 0";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
            int affected = cmd.ExecuteNonQuery();
            return affected > 0;
        }
    }

    /// <summary>
    /// Назначить флориста на заказ.
    /// </summary>
    public void AssignFlorist(int orderId, int floristId)
    {
        using SqlConnection conn = _database.Open();
        string sql = @"
            UPDATE dbo.Orders
            SET FloristId = @floristId, Status = @status
            WHERE Id = @orderId";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@floristId", floristId);
            cmd.Parameters.AddWithValue("@orderId", orderId);
            cmd.Parameters.AddWithValue("@status", OrderStatus.FloristAssigned);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// ЛР №4: создать заказ и зарезервировать букет как единую операцию.
    /// Параметр useTransaction указывает на использование явной транзакции.
    /// При успехе обновляет заказ и букет; при ошибке откатывает оба изменения.
    /// simulatedError - имитирует ошибку внутри транзакции для демонстрации rollback.
    /// </summary>
    public PlaceOrderOutcome PlaceOrderAndReserve(
        int customerId,
        int bouquetId,
        string deliveryAddress,
        DateTime deliveryDate,
        bool useTransaction = true,
        bool simulatedError = false)
    {
        using SqlConnection conn = _database.Open();

        SqlTransaction? transaction = null;
        try
        {
            if (useTransaction)
            {
                transaction = conn.BeginTransaction(IsolationLevel.ReadCommitted);
            }

            // Шаг 1: Получить текущее количество доступных букетов
            int availableBefore = GetAvailableCountInTransaction(conn, bouquetId, transaction);

            // Шаг 2: Проверить наличие
            if (availableBefore <= 0)
            {
                if (transaction != null) transaction.Rollback();
                throw new OperationException(ErrorCodes.OutOfStock, $"Букет #{bouquetId} недоступен");
            }

            // Шаг 3: Создать заказ
            string createOrderSql = @"
                INSERT INTO dbo.Orders (CustomerId, BouquetId, DeliveryDate, Status, DeliveryAddress)
                OUTPUT INSERTED.Id
                VALUES (@customerId, @bouquetId, @deliveryDate, @status, @address)";

            int orderId;
            using (var cmd = new SqlCommand(createOrderSql, conn, transaction))
            {
                cmd.Parameters.AddWithValue("@customerId", customerId);
                cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
                cmd.Parameters.AddWithValue("@deliveryDate", deliveryDate);
                cmd.Parameters.AddWithValue("@status", OrderStatus.Created.ToString());
                cmd.Parameters.AddWithValue("@address", deliveryAddress);

                object? result = cmd.ExecuteScalar();
                orderId = result != null ? (int)result : -1;
            }

            // Шаг 4: Имитация ошибки (для демонстрации rollback)
            if (simulatedError)
            {
                if (transaction != null)
                {
                    transaction.Rollback();
                    transaction = null;
                    throw new OperationException(ErrorCodes.TransactionError,
                        "Симулированная ошибка между операциями «создание заказа» и «резервирование букета»: " +
                        "выполнен ROLLBACK — заказ НЕ сохранён, ресурс НЕ изменён");
                }

                throw new OperationException(ErrorCodes.TransactionError,
                    "Симулированная ошибка между операциями «создание заказа» и «резервирование букета»: " +
                    "транзакции НЕ было, поэтому заказ уже сохранён в БД — частичное изменение данных!");
            }

            // Шаг 5: Зарезервировать букет (уменьшить AvailableCount).
            // Условие AvailableCount > 0 гарантирует, что ресурс не уйдёт в минус
            // (дополнительно защищает CHECK-ограничение CK_Bouquets_AvailableCount).
            string reserveSql = @"
                UPDATE dbo.Bouquets
                SET AvailableCount = AvailableCount - 1
                WHERE Id = @bouquetId AND AvailableCount > 0";

            int affected;
            using (var cmd = new SqlCommand(reserveSql, conn, transaction))
            {
                cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
                affected = cmd.ExecuteNonQuery();
            }

            if (affected == 0)
            {
                // Ресурс исчерпан конкурентным запросом — откатываем и сообщаем об ошибке.
                if (transaction != null)
                {
                    transaction.Rollback();
                    transaction = null;
                }
                throw new OperationException(ErrorCodes.OutOfStock,
                    $"Букет #{bouquetId} недоступен: свободных экземпляров не осталось");
            }

            // Шаг 6: Получить новое количество доступных букетов
            int availableAfter = GetAvailableCountInTransaction(conn, bouquetId, transaction);

            // Коммит транзакции
            if (transaction != null)
            {
                transaction.Commit();
            }

            return new PlaceOrderOutcome(orderId, bouquetId, availableBefore, availableAfter, useTransaction, false);
        }
        catch (Exception ex) when (!(ex is OperationException))
        {
            transaction?.Rollback();
            throw new OperationException(ErrorCodes.InternalError, $"Ошибка при размещении заказа: {ex.Message}");
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    /// <summary>
    /// Вспомогательный метод для получения количества доступных букетов внутри транзакции.
    /// </summary>
    private static int GetAvailableCountInTransaction(
        SqlConnection conn,
        int bouquetId,
        SqlTransaction? transaction)
    {
        string sql = "SELECT AvailableCount FROM dbo.Bouquets WHERE Id = @bouquetId";
        using (var cmd = new SqlCommand(sql, conn, transaction))
        {
            cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
            object? result = cmd.ExecuteScalar();
            return result != null ? (int)result : 0;
        }
    }

    /// <summary>
    /// Загрузить цветы для букета.
    /// </summary>
    private static List<FlowerItem> GetFlowerItems(SqlConnection conn, int bouquetId)
    {
        var flowers = new List<FlowerItem>();
        string sql = @"
            SELECT FlowerName, Count, PricePerUnit
            FROM dbo.FlowerItems
            WHERE BouquetId = @bouquetId
            ORDER BY Id";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@bouquetId", bouquetId);
            using SqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                flowers.Add(new FlowerItem
                {
                    FlowerName = reader.GetString(0),
                    Count = reader.GetInt32(1),
                    PricePerUnit = reader.GetDecimal(2)
                });
            }
        }

        return flowers;
    }
}

/// <summary>
/// Результат операции PlaceOrderAndReserve: содержит Id заказа,
/// количество букетов до и после резервирования, флаги режима выполнения.
/// </summary>
public sealed record PlaceOrderOutcome(
    int OrderId,
    int BouquetId,
    int AvailableBefore,
    int AvailableAfter,
    bool UseTransaction,
    bool SimulatedError);
