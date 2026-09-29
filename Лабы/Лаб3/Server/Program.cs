using FlowerShop.Server;

// ===== ЛР №3. Точка входа сервера. =====
// Механизм синхронизации общего ресурса выбирается аргументом --sync:
//   dotnet run --project Server -- --sync=lock        (основной вариант, Требование 5)
//   dotnet run --project Server -- --sync=semaphore   (альтернативный вариант, Требование 9)
//   dotnet run --project Server -- --sync=none        (поведение ЛР №2 — Race Condition)
// Первый числовой аргумент — порт (по умолчанию 5000).
int port = Server.DefaultPort;
SyncMechanism sync = SyncMechanism.Lock;

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
    else if (int.TryParse(arg, out int p))
    {
        port = p;
    }
}

var server = new Server(port, sync: sync);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Сервер] Остановка...");
    server.Stop();
    Environment.Exit(0);
};

Console.WriteLine($"[Сервер] ЛР №3: механизм синхронизации общего ресурса — {sync}");

// ЛР №2: сервер работает асинхронно (async/await), приём подключений не блокирует
// обработку — каждый клиент обслуживается в отдельной Task.
await server.StartAsync();
