using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server.Data;

/// <summary>
/// ЛР №4: подключение сервера к Microsoft SQL Server (СУБД выбрана по условию —
/// контейнер mssql/server:2022-latest). Клиент НЕ обращается к БД напрямую:
/// архитектура Client → Server → Database.
///
/// Класс отвечает за:
///  - строку подключения (сервер, порт, sa, пароль, БД FlowerShop);
///  - открытие соединений с обработкой ошибок подключения (Требование 8);
///  - создание схемы БД и демонстрационных данных из schema.sql.
/// </summary>
public class Database
{
    /// <summary>Имя основной базы данных (Primary).</summary>
    public const string DefaultDatabase = "FlowerShop";

    /// <summary>Имя резервной базы данных (Replica) — независимая копия Primary.</summary>
    public const string ReplicaDatabase = "FlowerShopReplica";

    /// <summary>Имя базы данных этого экземпляра (FlowerShop или FlowerShopReplica).</summary>
    public string DatabaseName { get; }

    /// <summary>Строка подключения к контейнеру SQL Server (docker, порт 1433).</summary>
    public string ConnectionString { get; }

    /// <summary>Последняя ошибка подключения (для журнала и диагностики).</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Создать объект доступа к экземпляру БД. <paramref name="databaseName"/> —
    /// имя базы (Primary или Replica). Моделирование отказа: если
    /// <paramref name="forceUnavailable"/> = true, любое подключение бросает
    /// <see cref="OperationException"/> (Требование 2 части 3 — искусственный отказ).
    /// </summary>
    public Database(string? databaseName = null, string? connectionString = null,
        bool forceUnavailable = false)
    {
        DatabaseName = databaseName ?? DefaultDatabase;
        ConnectionString = connectionString ?? BuildConnectionString(DatabaseName);
        ForceUnavailable = forceUnavailable;
    }

    /// <summary>
    /// ЛР №5: искусственно сделать экземпляр недоступным (моделирование отказа).
    /// При значении true любая операция с БД бросает <see cref="OperationException"/>.
    /// </summary>
    public bool ForceUnavailable { get; set; }

    /// <summary>
    /// Строка подключения по умолчанию: контейнер mssql/server:2022-latest
    /// на localhost:1433, вход sa / MyStrongPassword123!, указанная база.
    /// Переопределяется переменной окружения FLOWERSHOP_DB.
    /// </summary>
    public static string BuildConnectionString(string databaseName = DefaultDatabase)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("FLOWERSHOP_DB");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost,1433",
            InitialCatalog = databaseName,
            UserID = "sa",
            Password = "MyStrongPassword123!",
            Encrypt = false,          // локальный контейнер без TLS-сертификата
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            Pooling = true
        }.ConnectionString;
    }

    /// <summary>
    /// Открыть соединение с БД. Бросает <see cref="OperationException"/>
    /// с кодом <see cref="ErrorCodes.ServerUnavailable"/>, если соединение
    /// установить не удалось (Требование 8: ошибка подключения к БД).
    /// ЛР №5: если экземпляр помечен недоступным (<see cref="ForceUnavailable"/>),
    /// бросает <see cref="ErrorCodes.PrimaryUnavailable"/> — модель отказа.
    /// </summary>
    public SqlConnection Open()
    {
        if (ForceUnavailable)
        {
            throw new OperationException(
                DatabaseName == ReplicaDatabase ? ErrorCodes.ReplicaUnavailable : ErrorCodes.PrimaryUnavailable,
                $"Экземпляр '{DatabaseName}' недоступен (смоделирован отказ)");
        }

        var connection = new SqlConnection(ConnectionString);
        try
        {
            connection.Open();
            LastError = null;
            return connection;
        }
        catch (SqlException ex)
        {
            LastError = ex.Message;
            connection.Dispose();
            throw new OperationException(
                ErrorCodes.ServerUnavailable,
                $"Ошибка подключения к БД '{DatabaseName}': {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            LastError = ex.Message;
            connection.Dispose();
            throw new OperationException(
                ErrorCodes.ServerUnavailable,
                $"Соединение с БД '{DatabaseName}' отсутствует: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверить доступность БД без исключений: true — соединение установлено.
    /// Используется сервером при старте, для определения состояния экземпляра и в тестах.
    /// </summary>
    public bool TryPing(out string error)
    {
        if (ForceUnavailable)
        {
            error = $"Экземпляр '{DatabaseName}' помечен как недоступный (смоделирован отказ)";
            return false;
        }

        try
        {
            using SqlConnection conn = Open();
            error = string.Empty;
            return true;
        }
        catch (OperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Создать базу данных <see cref="DatabaseName"/>, если она ещё не существует.
    /// Подключение выполняется к master, т.к. к несуществующей БД подключиться нельзя.
    /// Вызывается сервером при старте ДО проверки доступности (Требование 8).
    /// </summary>
    public void EnsureDatabaseExists()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        using var conn = new SqlConnection(builder.ConnectionString);
        try
        {
            conn.Open();
        }
        catch (SqlException ex)
        {
            throw new OperationException(ErrorCodes.ServerUnavailable,
                $"Соединение с сервером СУБД отсутствует: {ex.Message}");
        }

        using var cmd = new SqlCommand($"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];", conn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Создать схему БД (schema.sql): разбивает скрипт по GO и выполняет
    /// каждый батч отдельно. Идемпотентно — повторный запуск безопасен.
    /// </summary>
    public void EnsureSchema(string schemaSqlPath)
    {
        if (!File.Exists(schemaSqlPath))
        {
            throw new OperationException(
                ErrorCodes.InternalError,
                $"Файл схемы не найден: {schemaSqlPath}");
        }

        string script = File.ReadAllText(schemaSqlPath, Encoding.UTF8);

        // GO — разделитель батчей sqlcmd; не является частью T-SQL.
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (string line in script.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Length > 0) { batches.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.AppendLine(line);
            }
        }
        if (current.Length > 0) batches.Add(current.ToString());

        using SqlConnection conn = Open();
        foreach (string batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            try
            {
                using var cmd = new SqlCommand(batch, conn);
                cmd.CommandTimeout = 30;
                cmd.ExecuteNonQuery();
            }
            catch (SqlException ex)
            {
                throw new OperationException(
                    ErrorCodes.InternalError,
                    $"Ошибка выполнения schema.sql: {ex.Message}");
            }
        }
    }
}
