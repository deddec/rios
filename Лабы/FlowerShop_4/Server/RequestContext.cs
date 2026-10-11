namespace FlowerShop.Server;

/// <summary>
/// Контекст выполнения запроса (ЛР №3). Передаётся из сервера в бизнес-логику,
/// чтобы операции с общим ресурсом могли писать в журнал критические события:
/// LOCK, UNLOCK, READ, WRITE, SUCCESS, FAIL, TIME (Требование 8).
/// </summary>
public sealed class RequestContext
{
    /// <summary>Номер клиента, которому принадлежит запрос (Client 1, Client 2, …).</summary>
    public int ClientId { get; init; }

    /// <summary>
    /// Обработчик журнальных записей: (фаза, описание). Сервер печатает строку в консоль
    /// и добавляет запись в <c>Server.Journal</c>. В unit-тестах бизнес-логики может быть null.
    /// </summary>
    public Action<string, string>? Journal { get; init; }

    /// <summary>Отображаемое имя клиента для сообщений журнала.</summary>
    public string ClientTag => $"Client {ClientId}";
}
