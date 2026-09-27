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
}
