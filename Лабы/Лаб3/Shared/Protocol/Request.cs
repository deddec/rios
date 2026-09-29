using System.Text.Json;

namespace FlowerShop.Shared.Protocol;

/// <summary>
/// Запрос клиента серверу: имя операции и данные (payload) в виде JSON.
/// </summary>
public class Request
{
    /// <summary>Имя операции, например "CreateOrder".</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Данные запроса (произвольный JSON-объект).</summary>
    public JsonElement? Payload { get; set; }
}
