using FlowerShop.Server;

var port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : Server.DefaultPort;
var server = new Server(port);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Сервер] Остановка...");
    server.Stop();
    Environment.Exit(0);
};

// ЛР №2: сервер работает асинхронно (async/await), приём подключений не блокирует
// обработку — каждый клиент обслуживается в отдельной Task.
await server.StartAsync();
