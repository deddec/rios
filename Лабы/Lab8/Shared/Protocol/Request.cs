using System.Text.Json.Serialization;

namespace FlowerShop.Shared.Protocol;

/// <summary>Запрос клиента к серверу (JSON-протокол).</summary>
public class Request
{
    public string Operation { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
}

/// <summary>Ответ сервера клиенту (JSON-протокол).</summary>
public class Response
{
    public bool Success { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public JsonElement? Data { get; init; }

    public static Response Ok(object? data = null, string? message = null) => new()
    {
        Success = true,
        Message = message ?? "Операция выполнена успешно",
        Data = data is null ? null : JsonSerializer.SerializeToElement(data)
    };

    public static Response Fail(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Message = message
    };
}

/// <summary>Настройки сериализации JSON для протокола.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}