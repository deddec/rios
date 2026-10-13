using System.Text.Json;

namespace FlowerShop.Shared.Protocol;

/// <summary>
/// Ответ сервера клиенту: признак успеха, код ошибки, сообщение и данные.
/// </summary>
public class Response
{
    /// <summary>true — операция выполнена успешно.</summary>
    public bool Success { get; set; }

    /// <summary>Код ошибки (см. ErrorCodes), null при успехе.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Сообщение для пользователя (описание результата или ошибки).</summary>
    public string? Message { get; set; }

    /// <summary>Данные-результат (произвольный JSON-объект).</summary>
    public JsonElement? Data { get; set; }

    public static Response Ok(object? data = null, string? message = null) => new()
    {
        Success = true,
        Message = message,
        // Данные ответа сериализуются с camelCase-именами полей (см. Json.DataOptions)
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, Json.DataOptions)
    };

    public static Response Fail(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Message = message
    };
}
