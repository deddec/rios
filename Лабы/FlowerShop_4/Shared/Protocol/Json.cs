using System.Text.Json;

namespace FlowerShop.Shared.Protocol;

/// <summary>
/// Единые настройки сериализации JSON для клиента и сервера.
/// </summary>
public static class Json
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Настройки сериализации данных ответа (поле <c>Data</c>): имена свойств приводятся
    /// к camelCase, чтобы поля читались единообразно на клиенте и в тестах
    /// (например, <c>availableCount</c> вместо <c>AvailableCount</c>).
    /// </summary>
    public static JsonSerializerOptions DataOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
