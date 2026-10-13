using System.Diagnostics;
using FlowerShop.Client;
using FlowerShop.Server;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// Обязательные тесты ЛР №2 (Требование 10), сохранённые в ЛР №3:
/// 1) один клиент; 2) два клиента; 3) параллельная обработка с задержкой 10 с;
/// 4) Race Condition на общем ресурсе БЕЗ синхронизации (режим SyncMechanism.None —
///    эксперимент «до исправления»; исправленное поведение проверяет SynchronizationTests).
/// </summary>
public class ParallelProcessingTests
{
    // ----- Тест 1. Один клиент: корректность обычной обработки -------------

    [Fact]
    public async Task OneClient_RequestIsProcessedCorrectly()
    {
        using var host = new TestServerHost();
        using var client = host.CreateClient();

        Response response = await client.SendAsync(
            RequestBuilder.CreateOrder(1, 1, "ул. Ленина, 10", "2026-10-05"));

        Assert.True(response.Success, response.Message);
        Assert.NotNull(response.Data);

        // В журнале есть START и END единственного клиента
        var journal = host.Server.Journal.ToArray();
        Assert.Contains(journal, e => e.ClientId == 1 && e.Phase == "START" && e.Operation == "CreateOrder");
        Assert.Contains(journal, e => e.ClientId == 1 && e.Phase == "END" && e.Operation == "CreateOrder");

        var start = journal.Single(e => e.ClientId == 1 && e.Phase == "START");
        var end = journal.Single(e => e.ClientId == 1 && e.Phase == "END");
        Assert.True(start.Timestamp <= end.Timestamp, "START должен быть не позже END");
    }

    // ----- Тест 2. Два клиента: сервер обрабатывает оба запроса ------------

    [Fact]
    public async Task TwoClients_BothRequestsAreProcessed()
    {
        using var host = new TestServerHost();
        using var client1 = host.CreateClient();
        using var client2 = host.CreateClient();

        // Оба запроса отправляются одновременно (Task.WhenAll)
        Task<Response> task1 = client1.SendAsync(
            RequestBuilder.CreateOrder(1, 1, "ул. Ленина, 10", "2026-10-05"));
        Task<Response> task2 = client2.SendAsync(
            RequestBuilder.CreateOrder(2, 2, "пр. Мира, 25", "2026-10-06"));

        Response[] results = await Task.WhenAll(task1, task2);

        Assert.All(results, r => Assert.True(r.Success, r.Message));

        // Сервер выделил двум клиентам разные идентификаторы и обработал обоих
        var journal = host.Server.Journal.ToArray();
        int[] clientIds = journal.Where(e => e.Phase == "END").Select(e => e.ClientId).Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 1, 2 }, clientIds);

        // Оба заказа действительно созданы
        Assert.Equal(2, host.Server.Logic.DbgOrders.Count);
    }

    // ----- Тест 3. Параллельная обработка (задержка 10 с) -------------------

    [Fact]
    public async Task TwoClients_WithTenSecondDelay_AreProcessedInParallel()
    {
        using var host = new TestServerHost();
        using var client1 = host.CreateClient();
        using var client2 = host.CreateClient();

        var stopwatch = Stopwatch.StartNew();

        // Client 1: запрос с искусственной задержкой 10 с (Требование 4)
        Task<Response> task1 = client1.SendAsync(
            RequestBuilder.CreateOrder(1, 1, "ул. Ленина, 10", "2026-10-05", delayMs: 10_000));

        // Пока Client 1 обрабатывается, отправляет запрос Client 2 (Требование 5)
        await Task.Delay(500);
        Task<Response> task2 = client2.SendAsync(
            RequestBuilder.CreateOrder(2, 2, "пр. Мира, 25", "2026-10-06", delayMs: 10_000));

        Response[] results = await Task.WhenAll(task1, task2);
        double totalSeconds = stopwatch.Elapsed.TotalSeconds;

        Assert.All(results, r => Assert.True(r.Success, r.Message));

        // Временные метки из журнала сервера
        var journal = host.Server.Journal.ToArray();
        var c1Start = journal.Single(e => e.ClientId == 1 && e.Phase == "START");
        var c1End = journal.Single(e => e.ClientId == 1 && e.Phase == "END");
        var c2Start = journal.Single(e => e.ClientId == 2 && e.Phase == "START");
        var c2End = journal.Single(e => e.ClientId == 2 && e.Phase == "END");

        // Доказательство параллелизма:
        // Client 2 НАЧАЛ обработку до завершения Client 1
        Assert.True(c2Start.Timestamp < c1End.Timestamp,
            $"Client 2 начал обработку ({c2Start.Timestamp:HH:mm:ss.fff}) " +
            "позже завершения Client 1 — запросы обрабатывались последовательно");

        // Общий זמן близок к 10 с, а не к 20 с (последовательно было бы ~20.5 с)
        Assert.True(totalSeconds < 19,
            $"Общее время {totalSeconds:F1} с — запросы обрабатывались не параллельно");

        // Перекрытие интервалов обработки: [c1Start..c1End] ∩ [c2Start..c2End] ≠ ∅
        DateTime overlapStart = c1Start.Timestamp > c2Start.Timestamp ? c1Start.Timestamp : c2Start.Timestamp;
        DateTime overlapEnd = c1End.Timestamp < c2End.Timestamp ? c1End.Timestamp : c2End.Timestamp;
        Assert.True(overlapStart < overlapEnd, "Интервалы обработки двух клиентов не перекрываются");
    }

    // ----- Тест 4. Race Condition на общем ресурсе --------------------------

    [Fact]
    public async Task TwoClients_ReserveLastBouquet_WithoutSync_BothSucceed_RaceConditionOccurs()
    {
        // SyncMechanism.None — поведение ЛР №2: синхронизации нет, Race Condition воспроизводится.
        // Этот тест фиксирует состояние «ДО исправления» (для сравнения в ЛР №3).
        using var host = new TestServerHost(SyncMechanism.None);
        using var admin = host.CreateClient();
        using var client1 = host.CreateClient();
        using var client2 = host.CreateClient();

        // Перед экспериментом устанавливаем количество ресурса = 1 (Требование 6)
        Response set = await admin.SendAsync(RequestBuilder.SetBouquetAvailability(1, 1));
        Assert.True(set.Success, set.Message);

        // Client 1 отправляет запрос бронирования
        Task<Response> task1 = client1.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 2000));

        // Пока Client 1 находится в обработке (окно гонки), такой же запрос Client 2
        await Task.Delay(300);
        Task<Response> task2 = client2.SendAsync(RequestBuilder.ReserveBouquet(1, criticalDelayMs: 2000));

        Response[] results = await Task.WhenAll(task1, task2);

        // Оба клиента получили SUCCESS, хотя ресурс был только один — Race Condition!
        Assert.All(results, r => Assert.True(r.Success,
            $"Оба клиента должны получить SUCCESS при Race Condition. Сообщение: {r.Message}"));

        // Состояние общего ресурса после эксперимента: 1 - 1 - 1 = -1
        // (оба клиента читали AvailableCount = 1 и оба уменьшили ресурс)
        Response status = await admin.SendAsync(RequestBuilder.GetBouquetStatus(1));
        Assert.True(status.Success, status.Message);
        int availableCount = status.Data!.Value.GetProperty("availableCount").GetInt32();

        Assert.True(availableCount == -1,
            $"Race Condition: ресурс ушёл в минус (ожидалось -1, получено {availableCount}) — " +
            "два успеха при количестве 1 (проблема намеренно не исправлена)");

        // Client 2 начал бронирование до завершения Client 1 (окно гонки).
        // Определяем нужные записи по операции: admin-клиент (SetBouquetAvailability)
        // подключился раньше и получил собственный ClientId.
        var reserveStarts = host.Server.Journal
            .Where(e => e.Phase == "START" && e.Operation == "ReserveBouquet")
            .OrderBy(e => e.Timestamp).ToArray();
        var reserveEnds = host.Server.Journal
            .Where(e => e.Phase == "END" && e.Operation == "ReserveBouquet")
            .OrderBy(e => e.Timestamp).ToArray();

        Assert.Equal(2, reserveStarts.Length);
        Assert.Equal(2, reserveEnds.Length);

        // Второй бронирующий начал обрабатываться до завершения первого —
        // запросы выполнялись одновременно (иначе Race Condition невозможен)
        Assert.True(reserveStarts[1].Timestamp < reserveEnds[0].Timestamp,
            "Второй клиент должен начать бронирование до завершения первого (окно гонки)");
    }
}
