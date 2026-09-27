using System.Text.Json;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>Исключение предметной области с кодом ошибки протокола.</summary>
public class OperationException : Exception
{
    public string ErrorCode { get; }

    public OperationException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Принимает JSON-запрос, десериализует его, определяет операцию, выполняет
/// бизнес-логику и формирует JSON-ответ. Обрабатывает некорректные запросы.
/// ЛР №2: обработка асинхронная (async/await, Task) — поддерживает искусственную
/// задержку <c>delayMs</c> для эксперимента с параллельной обработкой (Требование 4).
/// </summary>
public class RequestHandler
{
    /// <summary>Максимально допустимая экспериментальная задержка обработки, мс.</summary>
    public const int MaxDelayMs = 300_000;

    private readonly BusinessLogic _logic;

    public RequestHandler(BusinessLogic logic)
    {
        _logic = logic;
    }

    /// <summary>
    /// Имя операции из JSON-запроса без полной обработки — используется сервером
    /// для лога START/END (Требование 3). "?" — если JSON некорректен.
    /// </summary>
    public static string TryGetOperationName(string requestJson)
    {
        try
        {
            Request? r = JsonSerializer.Deserialize<Request>(requestJson, Json.Options);
            return string.IsNullOrWhiteSpace(r?.Operation) ? "?" : r.Operation;
        }
        catch (JsonException)
        {
            return "?";
        }
    }

    /// <summary>
    /// Асинхронная обработка одного запроса: строка JSON на входе, строка JSON на выходе.
    /// Если в payload указано поле <c>delayMs</c>, перед выполнением операции делается
    /// искусственная задержка (эксперимент ЛР №2, Требование 4).
    /// </summary>
    public async Task<Response> HandleAsync(string requestJson, CancellationToken ct = default)
    {
        try
        {
            Request? request;
            try
            {
                request = JsonSerializer.Deserialize<Request>(requestJson, Json.Options);
            }
            catch (JsonException)
            {
                return Response.Fail(ErrorCodes.ValidationError, "Некорректный формат JSON-запроса");
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Operation))
            {
                return Response.Fail(ErrorCodes.ValidationError, "Не указана операция (поле 'operation')");
            }

            int delayMs = ReadDelayMs(request.Payload);
            if (delayMs > 0)
            {
                // Искусственное увеличение времени обработки (Требование 4).
                // Задержка нужна только для проведения эксперимента.
                Console.WriteLine($"[Сервер] Экспериментальная задержка обработки: {delayMs} мс...");
                await Task.Delay(delayMs, ct);
            }

            return await _logic.ExecuteAsync(request, ct);
        }
        catch (OperationException ex)
        {
            return Response.Fail(ex.ErrorCode, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Response.Fail(ErrorCodes.InternalError, "Обработка запроса была отменена");
        }
        catch (Exception ex)
        {
            return Response.Fail(ErrorCodes.InternalError, $"Внутренняя ошибка сервера: {ex.Message}");
        }
    }

    /// <summary>Синхронная обработка (обёртка над <see cref="HandleAsync"/>).</summary>
    public Response Handle(string requestJson) => HandleAsync(requestJson).GetAwaiter().GetResult();

    /// <summary>Чтение опциональной экспериментальной задержки из payload (0 — задержки нет).</summary>
    private static int ReadDelayMs(JsonElement? payload)
    {
        if (payload is not JsonElement p ||
            p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("delayMs", out var v))
        {
            return 0;
        }

        int delayMs;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) delayMs = n;
        else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) delayMs = s;
        else throw new OperationException(ErrorCodes.ValidationError, "Поле 'delayMs' должно быть целым числом (миллисекунды)");

        if (delayMs < 0 || delayMs > MaxDelayMs)
        {
            throw new OperationException(ErrorCodes.ValidationError,
                $"Поле 'delayMs' должно быть в диапазоне 0..{MaxDelayMs} мс");
        }
        return delayMs;
    }
}
