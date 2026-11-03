namespace FlowerShop.Shared.Protocol;

/// <summary>Обёртка для удобного доступа к настройкам JSON-сериализации.</summary>
public static class JsonProtocol
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}