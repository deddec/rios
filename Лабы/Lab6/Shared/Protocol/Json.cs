using System.Text.Json;

namespace FlowerShop.Shared.Protocol;

/// <summary>
/// Единые настройки сериализации JSON для клиента и сервера.
/// Имена свойств — camelCase (конвенция JSON), чтобы протокол был единообразен:
/// имена полей ответа (<c>success</c>, <c>errorCode</c>, <c>message</c>, <c>data</c>)
/// и запроса (<c>operation</c>, <c>payload</c>) читаются одинаково на клиенте, сервере,
/// в дымовом драйвере (_smoke) и в тестах. Десериализация дополнительно
/// регистронезависима (<see cref="PropertyNameCaseInsensitive"/>).
/// </summary>
public static class Json
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
