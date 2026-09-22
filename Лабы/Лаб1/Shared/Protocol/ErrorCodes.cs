namespace FlowerShop.Shared.Protocol;

/// <summary>
/// Коды ошибок, передаваемые в ответах сервера (Требование 8 — обработка ошибок).
/// </summary>
public static class ErrorCodes
{
    /// <summary>Сервер недоступен (не удалось установить соединение).</summary>
    public const string ServerUnavailable = "SERVER_UNAVAILABLE";

    /// <summary>Некорректная (неизвестная) операция.</summary>
    public const string UnknownOperation = "UNKNOWN_OPERATION";

    /// <summary>Отсутствующий объект (заказ, флорист и т.п. не найдены).</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>Некорректные входные данные.</summary>
    public const string ValidationError = "VALIDATION_ERROR";

    /// <summary>Ошибка сетевого взаимодействия.</summary>
    public const string NetworkError = "NETWORK_ERROR";

    /// <summary>Внутренняя ошибка сервера.</summary>
    public const string InternalError = "INTERNAL_ERROR";
}
