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
/// </summary>
public class RequestHandler
{
    private readonly BusinessLogic _logic;

    public RequestHandler(BusinessLogic logic)
    {
        _logic = logic;
    }

    /// <summary>Обработка одного запроса: строка JSON на входе, строка JSON на выходе.</summary>
    public Response Handle(string requestJson)
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

            return _logic.Execute(request);
        }
        catch (OperationException ex)
        {
            return Response.Fail(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            return Response.Fail(ErrorCodes.InternalError, $"Внутренняя ошибка сервера: {ex.Message}");
        }
    }
}
