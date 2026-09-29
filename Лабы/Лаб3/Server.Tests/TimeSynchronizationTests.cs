using FlowerShop.Client;
using FlowerShop.Server;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// Тесты ЛР №3, часть 2 (Требование 11): синхронизация времени клиента с сервером.
/// Проверяются: получение времени сервера (TIME_REQUEST/TIME_RESPONSE), расчёт поправки
/// при смещённых часах клиента, остаточная ошибка после синхронизации,
/// влияние искусственной задержки сервера и обработка некорректных данных времени.
/// </summary>
public class TimeSynchronizationTests
{
    /// <summary>Допустимая погрешность измерений на loopback (мс).</summary>
    private const double ToleranceMs = 500;

    // ----- Тест 1. Сервер возвращает своё текущее время ---------------------

    [Fact]
    public async Task GetServerTime_ReturnsTimeCloseToServerUtcNow()
    {
        using var host = new TestServerHost();
        using var client = host.CreateClient();

        DateTime before = DateTime.UtcNow;
        Response response = await client.SendAsync(RequestBuilder.GetServerTime());
        DateTime after = DateTime.UtcNow;

        Assert.True(response.Success, response.Message);

        DateTime serverTime = TimeSynchronizer.ParseServerTime(response);
        Assert.InRange(serverTime, before.AddSeconds(-1), after.AddSeconds(1));
    }

    // ----- Тест 2. Расчёт поправки при смещённых часах клиента --------------

    [Theory]
    [InlineData(7)]    // часы клиента «спешат» на 7 с (пример из методички)
    [InlineData(-5)]   // часы клиента «отстают» на 5 с
    public async Task SyncAsync_WithClockSkew_ComputesOffsetAndCorrectsTime(double skewSeconds)
    {
        using var host = new TestServerHost();
        using var client = host.CreateClient();

        TimeSpan skew = TimeSpan.FromSeconds(skewSeconds);
        var clock = new ClientClock { ClockOffset = skew };      // модель рассинхронизации
        var synchronizer = new TimeSynchronizer(client, clock);

        // До синхронизации локальное время отличается от эталонного на величину смещения
        Assert.True(Math.Abs((clock.LocalNow - DateTime.UtcNow - skew).TotalMilliseconds) < 200,
            "Локальные часы клиента должны быть смещены на заданную величину");

        TimeSyncResult result = await synchronizer.SyncAsync();

        // Вычисленная поправка противоположна смещению часов: Offset ≈ −skew
        TimeSpan offsetError = result.CalculatedOffset + skew;
        Assert.True(Math.Abs(offsetError.TotalMilliseconds) < ToleranceMs,
            $"Поправка {result.CalculatedOffset.TotalSeconds:F3} с неточна (ожидалось ≈ {-skew.TotalSeconds:F0} с)");

        // После синхронизации скорректированное время совпадает с временем сервера
        TimeSpan residual = await synchronizer.MeasureResidualErrorAsync();
        Assert.True(Math.Abs(residual.TotalMilliseconds) < ToleranceMs,
            $"Остаточная ошибка после синхронизации: {residual.TotalMilliseconds:F0} мс");

        // Поправка сохранена в часах клиента (Требование 4)
        Assert.True(clock.IsSynchronized);
        Assert.Equal(result.CalculatedOffset, clock.Correction);

        // «Сырое» локальное время по-прежнему смещено — поправка его не изменяет:
        // синхронизация выполняется «поверх» часов, а не переводит их физически
        Assert.True(Math.Abs((clock.LocalNow - DateTime.UtcNow - skew).TotalMilliseconds) < 200);
    }

    // ----- Тест 3. Влияние искусственной задержки сервера (Требование 7) ----

    [Fact]
    public async Task SyncAsync_WithServerDelay_ReducesAccuracy()
    {
        using var host = new TestServerHost();
        using var client = host.CreateClient();

        TimeSpan skew = TimeSpan.FromSeconds(7);
        var clock = new ClientClock { ClockOffset = skew };
        var synchronizer = new TimeSynchronizer(client, clock);

        const int serverDelayMs = 1500;
        TimeSyncResult result = await synchronizer.SyncAsync(serverDelayMs);

        // RTT включает искусственную задержку сервера
        Assert.True(result.Rtt >= TimeSpan.FromMilliseconds(serverDelayMs - 200),
            $"RTT {result.Rtt.TotalMilliseconds:F0} мс должен включать задержку сервера {serverDelayMs} мс");

        // Задержка нарушает симметрию обмена: поправка ошибается примерно на delay/2
        // (оценка RTT/2 не компенсирует задержку обработки на сервере)
        TimeSpan offsetError = result.CalculatedOffset + skew;
        Assert.True(offsetError > TimeSpan.FromMilliseconds(300),
            $"Ошибка поправки {offsetError.TotalMilliseconds:F0} мс слишком мала для задержки {serverDelayMs} мс");
        Assert.True(offsetError < TimeSpan.FromSeconds(1.2),
            $"Ошибка поправки {offsetError.TotalMilliseconds:F0} мс превышает ожидаемую ≈ delay/2");

        // Скорректированное время при этом «убегает» вперёд — остаточная ошибка отрицательна
        TimeSpan residual = await synchronizer.MeasureResidualErrorAsync();
        Assert.True(residual < TimeSpan.FromMilliseconds(-300),
            $"Остаточная ошибка {residual.TotalMilliseconds:F0} мс — ожидалось ≈ −delay/2");
    }

    // ----- Тест 4. Проблема рассинхронизации без синхронизации --------------

    [Fact]
    public async Task MeasureResidualError_BeforeSynchronization_ShowsClockSkew()
    {
        using var host = new TestServerHost();
        using var client = host.CreateClient();

        // Часы клиента отстают на 5 с; синхронизация ещё не выполнялась
        var clock = new ClientClock { ClockOffset = TimeSpan.FromSeconds(-5) };
        var synchronizer = new TimeSynchronizer(client, clock);

        // Скорректированное время равно локальному, поэтому остаточная ошибка
        // показывает само смещение часов: +5 с (Требование 2 — демонстрация проблемы)
        TimeSpan residual = await synchronizer.MeasureResidualErrorAsync();
        Assert.True(Math.Abs(residual.TotalSeconds - 5) < 0.5,
            $"Ожидалось ≈ +5.0 с (смещение часов клиента), получено {residual.TotalSeconds:F2} с");
    }

    // ----- Тест 5. Некорректные данные времени (Требование 11) --------------

    [Fact]
    public void ParseServerTime_WithInvalidData_ThrowsTimeSynchronizationException()
    {
        // В ответе нет полей времени
        Response withoutTime = Response.Ok(new { somethingElse = 1 });
        TimeSynchronizationException ex =
            Assert.Throws<TimeSynchronizationException>(() => TimeSynchronizer.ParseServerTime(withoutTime));
        Assert.Equal(ErrorCodes.TimeSyncError, ex.ErrorCode);

        // Ответ вообще без данных (ошибка сервера)
        Response withoutData = Response.Fail(ErrorCodes.ValidationError, "нет данных");
        Assert.Throws<TimeSynchronizationException>(() => TimeSynchronizer.ParseServerTime(withoutData));
    }
}
