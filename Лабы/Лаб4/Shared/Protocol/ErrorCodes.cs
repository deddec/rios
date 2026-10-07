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

    /// <summary>
    /// Ресурс исчерпан (ЛР №2/№3): запрошенное количество единиц общего ресурса
    /// (например, доступных букетов) равно нулю — бронирование отклонено.
    /// </summary>
    public const string OutOfStock = "OUT_OF_STOCK";

    /// <summary>
    /// Ошибка синхронизации времени (ЛР №3, часть 2): сервер вернул некорректные
    /// данные времени или ответ не содержит поля serverTime.
    /// </summary>
    public const string TimeSyncError = "TIME_SYNC_ERROR";
}
