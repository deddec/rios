using FlowerShop.Server;
using FlowerShop.Server.Data;

// ===== ЛР №4. Точка входа сервера с подключением к MSSQL. =====
// Механизм синхронизации общего ресурса выбирается аргументом --sync:
//   dotnet run --project Server -- --sync=lock        (основной вариант, Требование 5)
//   dotnet run --project Server -- --sync=semaphore   (альтернативный вариант, Требование 9)
//   dotnet run --project Server -- --sync=none        (поведение ЛР №2 — Race Condition)
// Первый числовой аргумент — порт (по умолчанию 5000).

int port = Server.DefaultPort;
SyncMechanism sync = SyncMechanism.Lock;
bool skipDbInit = false;

foreach (string arg in args)
{
    if (arg.StartsWith("--sync=", StringComparison.OrdinalIgnoreCase))
    {
        string raw = arg["--sync=".Length..];
        if (!Enum.TryParse(raw, ignoreCase: true, out sync))
        {
            Console.WriteLine($"Неизвестный механизм синхронизации: '{raw}'. Доступны: none, lock, semaphore");
            return;
        }
    }
    else if (arg.Equals("--skip-db-init", StringComparison.OrdinalIgnoreCase))
    {
        skipDbInit = true;
    }
    else if (int.TryParse(arg, out int p))
    {
        port = p;
    }
}

// ЛР №4: инициализация подключения к MSSQL
Console.WriteLine("[Сервер] ЛР №4: инициализация подключения к MSSQL...");
var database = new Database();

// Создать БД FlowerShop, если её ещё нет (подключение к master)
try
{
    database.EnsureDatabaseExists();
}
catch (OperationException ex)
{
    Console.WriteLine($"[Сервер] ОШИБКА: сервер СУБД недоступен — {ex.Message}");
    Console.WriteLine("[Сервер] Убедитесь, что запущен контейнер mssql/server:2022-latest:");
    Console.WriteLine("[Сервер] docker run -e 'ACCEPT_EULA=Y' -e 'MSSQL_SA_PASSWORD=MyStrongPassword123!' -p 1433:1433 --name flowershop-db -d mcr.microsoft.com/mssql/server:2022-latest");
    return;
}

// Проверить доступность БД (Требование 8)
if (!database.TryPing(out string dbError))
{
    Console.WriteLine($"[Сервер] ОШИБКА: БД недоступна — {dbError}");
    Console.WriteLine("[Сервер] Убедитесь, что контейнер mssql/server:2022-latest запущен на localhost:1433");
    return;
}

Console.WriteLine("[Сервер] БД доступна ✓");

// Инициализация схемы БД (если флаг --skip-db-init не установлен)
if (!skipDbInit)
{
    try
    {
        // schema.sql копируется в выходную каталог проекта Server (см. csproj)
        string schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
        if (File.Exists(schemaPath))
        {
            Console.WriteLine("[Сервер] Создание схемы БД...");
            database.EnsureSchema(schemaPath);
            Console.WriteLine("[Сервер] Схема БД инициализирована ✓");
        }
        else
        {
            Console.WriteLine($"[Сервер] ВНИМАНИЕ: schema.sql не найден по пути {schemaPath}");
        }
    }
    catch (OperationException ex)
    {
        Console.WriteLine($"[Сервер] ОШИБКА при инициализации схемы: {ex.Message}");
        return;
    }
}

// Создать репозиторий заказов и передать его в бизнес-логику (ЛР №4):
// сервер — единственная сторона, работающая с БД (Client → Server → Database).
var orderRepository = new OrderRepository(database);
Console.WriteLine("[Сервер] Репозиторий заказов инициализирован ✓");

var logic = new BusinessLogic(sync: sync, repository: orderRepository);
var server = new Server(port, logic: logic);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Сервер] Остановка...");
    server.Stop();
    Environment.Exit(0);
};

Console.WriteLine($"[Сервер] ЛР №4: механизм синхронизации общего ресурса — {sync}");
Console.WriteLine($"[Сервер] Сервер запущен на порту {port}");

// ЛР №2-№4: сервер работает асинхронно (async/await), приём подключений не блокирует
// обработку — каждый клиент обслуживается в отдельной Task.
await server.StartAsync();
