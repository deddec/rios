using FlowerShop.Client;
using FlowerShop.Server;
using ShopServer = FlowerShop.Server.Server;

namespace FlowerShop.Tests;

/// <summary>
/// Тестовый хост: поднимает реальный сервер на эфемерном порту в фоновой Task
/// и позволяет создавать клиентов.
/// ЛР №3: механизм синхронизации общего ресурса выбирается параметром конструктора
/// (<see cref="SyncMechanism.Lock"/>, <see cref="SyncMechanism.SemaphoreSlim"/>
/// или <see cref="SyncMechanism.None"/>), поэтому один и тот же тест проверяет
/// оба варианта синхронизации.
/// </summary>
public sealed class TestServerHost : IDisposable
{
    public ShopServer Server { get; }
    private readonly Task _runLoop;

    public TestServerHost(SyncMechanism sync = SyncMechanism.Lock)
    {
        Server = new ShopServer(0, sync: sync); // 0 — эфемерный порт, выберется автоматически
        // До первого await (AcceptTcpClientAsync) тело выполнится синхронно,
        // поэтому к возврату listener уже запущен и Port доступен.
        _runLoop = Server.StartAsync();
    }

    public NetworkClient CreateClient() => new("127.0.0.1", Server.Port);

    public void Dispose()
    {
        Server.Stop();
        try { _runLoop.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* нормальная остановка */ }
    }
}
