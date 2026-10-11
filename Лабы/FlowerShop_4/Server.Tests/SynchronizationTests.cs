using FlowerShop.Client;
using FlowerShop.Server;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// Тесты ЛР №3, часть 1 (Требование 11): проверка синхронизации доступа к общему ресурсу.
///
/// Один и тот же сценарий выполняется для двух механизмов — lock (Требование 5)
/// и SemaphoreSlim (Требование 9). Оба должны давать одинаковый результат:
/// при одном свободном букете успех (SUCCESS) получает ровно один клиент,
/// второй получает отказ (FAIL, OUT_OF_STOCK), а ресурс не уходит в минус.
/// Состояние «до исправления» (Race Condition) проверяет
/// ParallelProcessingTests.TwoClients_ReserveLastBouquet_WithoutSync_…
/// </summary>
public class SynchronizationTests
{
    // ----- Тест 1. Взаимное исключение: успех только у одного клиента ------

    [Theory]
    [InlineData(SyncMechanism.Lock)]
    [InlineData(SyncMechanism.SemaphoreSlim)]
    public async Task TwoClients_ReserveLastBouquet_OnlyOneSucceeds(SyncMechanism sync)
    {
        using var host = new TestServerHost(sync);
        using var admin = host.CreateClient();
        using var client1 = host.CreateClient();
        using var client2 = host.CreateClient();

        // Перед экспериментом устанавливаем количество ресурса = 1
        Response set = await admin.SendAsync(RequestBuilder.SetBouquetAvailability(1, 1));
        Assert.True(set.Success, set.Message);

        // Client 1 входит в критическую секцию; Client 2 отправляет такую же заявку,
        // пока Client 1 ещё находится внутри секции
        Task<Response> task1 = client1.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 1500));
        await Task.Delay(200);
        Task<Response> task2 = client2.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 1500));

        Response[] results = await Task.WhenAll(task1, task2);

        // Ровно один SUCCESS, второй клиент — отказ (ресурс уже занят)
        Assert.Equal(1, results.Count(r => r.Success));
        Response failed = results.Single(r => !r.Success);
        Assert.Equal(ErrorCodes.OutOfStock, failed.ErrorCode);

        // Общий ресурс не ушёл в минус: 1 − 1 = 0 (в ЛР №2 здесь было −1)
        Response status = await admin.SendAsync(RequestBuilder.GetBouquetStatus(1));
        Assert.True(status.Success, status.Message);
        int available = status.Data!.Value.GetProperty("availableCount").GetInt32();
        Assert.Equal(0, available);

        // Требование 8: журнал содержит все события критической секции
        var journal = host.Server.Journal.ToArray();
        Assert.Contains(journal, e => e.Phase == "LOCK");
        Assert.Contains(journal, e => e.Phase == "UNLOCK");
        Assert.Contains(journal, e => e.Phase == "READ");
        Assert.Contains(journal, e => e.Phase == "WRITE");
        Assert.Contains(journal, e => e.Phase == "SUCCESS");
        Assert.Contains(journal, e => e.Phase == "FAIL");
    }

    // ----- Тест 2. Критические секции не пересекаются во времени -----------

    [Theory]
    [InlineData(SyncMechanism.Lock)]
    [InlineData(SyncMechanism.SemaphoreSlim)]
    public async Task TwoClients_CriticalSections_DoNotOverlap(SyncMechanism sync)
    {
        using var host = new TestServerHost(sync);
        using var admin = host.CreateClient();
        using var client1 = host.CreateClient();
        using var client2 = host.CreateClient();

        Response set = await admin.SendAsync(RequestBuilder.SetBouquetAvailability(1, 2));
        Assert.True(set.Success, set.Message);

        Task<Response> task1 = client1.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 700));
        await Task.Delay(150);
        Task<Response> task2 = client2.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 700));
        await Task.WhenAll(task1, task2);

        // Ожидаемый порядок событий: LOCK₁ → UNLOCK₁ → LOCK₂ → UNLOCK₂.
        // Это прямое доказательство взаимного исключения: пока один клиент внутри
        // критической секции, второй не может в неё войти.
        var critical = host.Server.Journal
            .Where(e => e.Phase is "LOCK" or "UNLOCK")
            .OrderBy(e => e.Timestamp)
            .ToArray();

        Assert.Equal(4, critical.Length);
        Assert.Equal("LOCK", critical[0].Phase);
        Assert.Equal("UNLOCK", critical[1].Phase);
        Assert.Equal("LOCK", critical[2].Phase);
        Assert.Equal("UNLOCK", critical[3].Phase);

        // Внутри одной пары LOCK/UNLOCK — один и тот же клиент
        Assert.Equal(critical[0].ClientId, critical[1].ClientId);
        Assert.Equal(critical[2].ClientId, critical[3].ClientId);
        Assert.NotEqual(critical[0].ClientId, critical[2].ClientId);

        // Выход первого клиента не позже входа второго — секции не пересекаются
        Assert.True(critical[1].Timestamp <= critical[2].Timestamp,
            $"Критические секции пересеклись: UNLOCK ({critical[1].Timestamp:HH:mm:ss.fff}) " +
            $"позже LOCK ({critical[2].Timestamp:HH:mm:ss.fff}) — взаимное исключение нарушено");
    }

    // ----- Тест 3. Синхронизация не блокирует весь сервер -------------------

    [Fact]
    public async Task SynchronizedReservations_DoNotBlockUnrelatedRequests()
    {
        using var host = new TestServerHost(SyncMechanism.Lock);
        using var admin = host.CreateClient();
        using var client1 = host.CreateClient();
        using var client2 = host.CreateClient();
        using var other = host.CreateClient();

        Response set = await admin.SendAsync(RequestBuilder.SetBouquetAvailability(1, 2));
        Assert.True(set.Success, set.Message);

        // Две заявки на общий ресурс удерживают критическую секцию (~1.5 с каждая)
        Task<Response> claim1 = client1.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 1500));
        await Task.Delay(100);
        Task<Response> claim2 = client2.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 1500));
        await Task.Delay(200);

        // Независимый запрос (не работает с общим ресурсом) не должен ждать блокировку:
        // критическая секция защищает только операцию с ресурсом, а не весь сервер
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Response independent = await other.SendAsync(
            RequestBuilder.CreateOrder(1, 1, "ул. Ленина, 10", "2026-10-05"));
        stopwatch.Stop();

        Assert.True(independent.Success, independent.Message);
        Assert.True(stopwatch.Elapsed.TotalSeconds < 1.0,
            $"Независимый запрос ждал {stopwatch.Elapsed.TotalSeconds:F2} с — " +
            "критическая секция оказалась слишком широкой (заблокирован весь сервер)");

        // Обе заявки на ресурс завершаются успешно (ресурса хватает на две)
        Response[] claims = await Task.WhenAll(claim1, claim2);
        Assert.All(claims, r => Assert.True(r.Success, r.Message));
    }
}
