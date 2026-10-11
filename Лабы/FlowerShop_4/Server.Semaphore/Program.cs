using FlowerShop.Server;
using ShopServer = FlowerShop.Server.Server;

// ===== ЛР №3, Требование 9: ещё один проект — замена lock на SemaphoreSlim. =====
//
// Основной проект (Server) защищает критическую секцию механизмом lock:
//
//     lock (_syncObject)
//     {
//         check();   // проверка доступности общего ресурса
//         update();  // изменение состояния ресурса
//     }
//
// Этот проект запускает тот же сервер, но с механизмом SyncMechanism.SemaphoreSlim,
// в котором критическая секция защищена семафором на одно разрешение:
//
//     await _semaphore.WaitAsync();
//     try     { check(); update(); }
//     finally { _semaphore.Release(); }
//
// Отличие: SemaphoreSlim ожидается АСИНХРОННО (поток не блокируется, внутри
// критической секции допустим await), а lock удерживает поток целиком.
// Оба варианта дают одинаковый результат: при одном свободном букете успех
// получает ровно один клиент.
//
// Запуск: dotnet run --project Server.Semaphore

int port = ShopServer.DefaultPort;
foreach (string arg in args)
{
    if (int.TryParse(arg, out int p)) port = p;
}

var server = new ShopServer(port, sync: SyncMechanism.SemaphoreSlim);

Console.WriteLine("=======================================================");
Console.WriteLine("  ЛР №3, Требование 9: сервер с синхронизацией SemaphoreSlim");
Console.WriteLine("  (в основном проекте Server используется lock)");
Console.WriteLine("=======================================================");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Сервер] Остановка...");
    server.Stop();
    Environment.Exit(0);
};

await server.StartAsync();
