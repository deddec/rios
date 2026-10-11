using System.Text.Json;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Client;

/// <summary>
/// Исключение синхронизации времени (ЛР №3, часть 2): сервер вернул некорректные
/// данные времени или запрос завершился ошибкой (Требование 11).
/// </summary>
public sealed class TimeSynchronizationException : Exception
{
    public string? ErrorCode { get; }

    public TimeSynchronizationException(string message, string? errorCode = null) : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Результат одного сеанса синхронизации времени — используется для вывода
/// на экран (Требование 8) и в unit-тестах (Требование 11).
/// </summary>
public sealed record TimeSyncResult(
    DateTime LocalBefore,            // t1 — локальное время клиента перед отправкой запроса
    DateTime LocalAfter,             // t4 — локальное время клиента после получения ответа
    DateTime ServerTime,             // serverTime — время сервера из TIME_RESPONSE
    TimeSpan Rtt,                    // Round-Trip Time = t4 - t1
    TimeSpan EstimatedNetworkDelay,  // оценка задержки в одну сторону = RTT / 2
    TimeSpan CalculatedOffset,       // Offset = ServerTime - (t1 + RTT/2)
    DateTime CorrectedTime,          // CorrectedTime = LocalTime + Offset
    int ServerDelayMs);              // искусственная задержка сервера при этом замере

/// <summary>
/// Синхронизация времени клиента со временем сервера (ЛР №3, часть 2).
///
/// Алгоритм (упрощённая модель NTP, Требования 3–6):
///   1) клиент фиксирует локальное время t1 и отправляет TIME_REQUEST (GetServerTime);
///   2) сервер фиксирует своё время serverTime и возвращает его (TIME_RESPONSE);
///   3) клиент фиксирует t4 и вычисляет RTT = t4 − t1;
///   4) считается, что задержка в обе стороны одинакова (Network delay ≈ RTT / 2),
///      поэтому момент сервера в шкале клиента — это t1 + RTT/2;
///   5) поправка Offset = serverTime − (t1 + RTT/2); клиент сохраняет её и далее
///      использует CorrectedTime = LocalTime + Offset.
///
/// Замечание: RTT измеряется по часам самого клиента, поэтому искусственное смещение
/// его часов взаимно сокращается и не влияет на оценку задержки.
/// </summary>
public sealed class TimeSynchronizer
{
    private readonly NetworkClient _client;
    private readonly ClientClock _clock;

    public TimeSynchronizer(NetworkClient client, ClientClock clock)
    {
        _client = client;
        _clock = clock;
    }

    /// <summary>Часы клиента, для которых выполняется синхронизация.</summary>
    public ClientClock Clock => _clock;

    /// <summary>
    /// Один сеанс синхронизации: TIME_REQUEST → TIME_RESPONSE → расчёт RTT и поправки.
    /// <paramref name="serverDelayMs"/> — искусственная задержка сервера перед фиксацией
    /// времени (эксперимент Требования 7: чем больше задержка, тем ниже точность).
    /// </summary>
    public async Task<TimeSyncResult> SyncAsync(int? serverDelayMs = null, CancellationToken ct = default)
    {
        DateTime t1 = _clock.LocalNow;                     // время отправки запроса
        Response response = await _client.SendAsync(RequestBuilder.GetServerTime(serverDelayMs), ct);
        DateTime t4 = _clock.LocalNow;                     // время получения ответа

        if (!response.Success)
        {
            throw new TimeSynchronizationException(
                $"Сервер не вернул время: [{response.ErrorCode}] {response.Message}", response.ErrorCode);
        }

        DateTime serverTime = ParseServerTime(response);

        TimeSpan rtt = t4 - t1;                            // Round-Trip Time
        TimeSpan networkDelay = rtt / 2;                   // RTT / 2 — оценка задержки в одну сторону
        DateTime referenceLocal = t1 + networkDelay;       // момент сервера в шкале клиента
        TimeSpan offset = serverTime - referenceLocal;     // Offset = ServerTime − ClientTime

        _clock.ApplyCorrection(offset);                    // клиент сохраняет поправку

        return new TimeSyncResult(
            LocalBefore: t1,
            LocalAfter: t4,
            ServerTime: serverTime,
            Rtt: rtt,
            EstimatedNetworkDelay: networkDelay,
            CalculatedOffset: offset,
            CorrectedTime: _clock.CorrectedNow,
            ServerDelayMs: serverDelayMs ?? 0);
    }

    /// <summary>
    /// Повторная проверка после синхронизации (Требование 8): клиент снова запрашивает
    /// время сервера и сравнивает его со своим скорректированным временем. Возвращает
    /// остаточную ошибку — чем ближе к нулю, тем точнее выполненная синхронизация.
    /// </summary>
    public async Task<TimeSpan> MeasureResidualErrorAsync(CancellationToken ct = default)
    {
        DateTime t1 = _clock.LocalNow;
        Response response = await _client.SendAsync(RequestBuilder.GetServerTime(), ct);
        DateTime t4 = _clock.LocalNow;

        if (!response.Success)
        {
            throw new TimeSynchronizationException(
                $"Сервер не вернул время: [{response.ErrorCode}] {response.Message}", response.ErrorCode);
        }

        DateTime serverTime = ParseServerTime(response);

        // Для честного сравнения берём середину интервала обмена и прибавляем поправку
        DateTime correctedMidpoint = t1 + (t4 - t1) / 2 + _clock.Correction;
        return serverTime - correctedMidpoint;
    }

    /// <summary>
    /// Разбор времени сервера из TIME_RESPONSE. Приоритет — Unix-миллисекунды
    /// (устойчивы к настройкам локали); строка ISO-8601 — запасной вариант.
    /// Требование 11: некорректные данные времени → TimeSynchronizationException.
    /// </summary>
    public static DateTime ParseServerTime(Response response)
    {
        if (response.Data is not { } data || data.ValueKind != JsonValueKind.Object)
        {
            throw new TimeSynchronizationException(
                "Ответ сервера не содержит данных времени", ErrorCodes.TimeSyncError);
        }

        if (data.TryGetProperty("serverTimeUnixMs", out var msEl) && msEl.TryGetInt64(out long unixMs))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        }

        if (data.TryGetProperty("serverTimeUtc", out var utcEl) && utcEl.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(utcEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return parsed.ToUniversalTime();
        }

        throw new TimeSynchronizationException(
            "Сервер вернул некорректные данные времени (нет поля serverTimeUnixMs)", ErrorCodes.TimeSyncError);
    }
}
