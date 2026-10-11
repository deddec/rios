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
    /// <summary>Имя базы данных по умолчанию.</summary>
    public const string DefaultDatabase = "FlowerShop";

    /// <summary>Строка подключения к контейнеру SQL Server (docker, порт 1433).</summary>
    public string ConnectionString { get; }

    /// <summary>Последняя ошибка подключения (для журнала и диагностики).</summary>
    public string? LastError { get; private set; }

    public Database(string? connectionString = null)
    {
        ConnectionString = connectionString ?? BuildConnectionString();
    }

    /// <summary>
    /// Строка подключения по умолчанию: контейнер mssql/server:2022-latest
    /// на localhost:1433, вход sa / MyStrongPassword, БД FlowerShop.
    /// Переопределяется переменной окружения FLOWERSHOP_DB.
    /// </summary>
    public static string BuildConnectionString()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("FLOWERSHOP_DB");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost,1433",
            InitialCatalog = DefaultDatabase,
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
    /// с кодом <see cref="ErrorCodes.DatabaseUnavailable"/>, если соединение
    /// установить не удалось (Требование 8: ошибка подключения к БД).
    /// </summary>
    public SqlConnection Open()
    {
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
                $"Ошибка подключения к БД: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            LastError = ex.Message;
            connection.Dispose();
            throw new OperationException(
                ErrorCodes.ServerUnavailable,
                $"Соединение с БД отсутствует: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверить доступность БД без исключений: true — соединение установлено.
    /// Используется сервером при старте и в тестах.
    /// </summary>
    public bool TryPing(out string error)
    {
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
    /// Создать базу данных FlowerShop, если она ещё не существует.
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

        using var cmd = new SqlCommand($"IF DB_ID(N'{DefaultDatabase}') IS NULL CREATE DATABASE [{DefaultDatabase}];", conn);
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
