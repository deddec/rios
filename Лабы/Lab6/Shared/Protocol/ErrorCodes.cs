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

    /// <summary>
    /// ЛР №4: ошибка базы данных (БД недоступна, нарушение ограничения, ошибка внутри
    /// транзакции). Сообщение содержит подробности от СУБД.
    /// </summary>
    public const string DatabaseError = "DB_ERROR";

    /// <summary>
    /// ЛР №4: составная операция завершилась ошибкой и была откатена (ROLLBACK),
    /// либо (без транзакции) оставила частичные изменения в БД.
    /// </summary>
    public const string TransactionError = "TRANSACTION_ERROR";

    // ===== ЛР №5: репликация и отказоустойчивость ========================

    /// <summary>
    /// ЛР №5: Primary (основной экземпляр) недоступен. Приложение обнаруживает отказ
    /// и переключается на Replica (Failover), поэтому один экземпляр не является
    /// единственной точкой отказа.
    /// </summary>
    public const string PrimaryUnavailable = "PRIMARY_UNAVAILABLE";

    /// <summary>
    /// ЛР №5: Replica (резервная копия) недоступна. Резервирование копии отсутствует,
    /// но приложение продолжает работать через Primary.
    /// </summary>
    public const string ReplicaUnavailable = "REPLICA_UNAVAILABLE";

    /// <summary>
    /// ЛР №5: недоступны ОБА экземпляра (Primary и Replica). Выполнить операцию
    /// невозможно — клиенту возвращается понятное сообщение об ошибке.
    /// </summary>
    public const string AllInstancesUnavailable = "ALL_INSTANCES_UNAVAILABLE";

    /// <summary>
    /// ЛР №5: ошибка репликации — не удалось передать изменения из активного
    /// экземпляра в резервный. Изменения остаются в активном экземпляре и будут
    /// синхронизированы после восстановления (Recovery).
    /// </summary>
    public const string ReplicationError = "REPLICATION_ERROR";

    /// <summary>
    /// ЛР №5: обнаружено расхождение данных между Primary и Replica (нарушение
    /// согласованности). Требуется синхронизация.
    /// </summary>
    public const string ConsistencyError = "CONSISTENCY_ERROR";

    /// <summary>
    /// ЛР №5: невозможность выполнить Failover — например, когда Primary недоступен,
    /// но резервный экземпляр тоже недоступен, поэтому переключаться некуда.
    /// </summary>
    public const string FailoverError = "FAILOVER_ERROR";

    // ===== ЛР №6: Pub/Sub (Publisher, Broker, Subscriber) ===================

    /// <summary>
    /// ЛР №6: Broker недоступен — Publisher не смог опубликовать событие
    /// (все Broker'ы, известные Publisher, недоступны).
    /// </summary>
    public const string BrokerUnavailable = "BROKER_UNAVAILABLE";

    /// <summary>
    /// ЛР №6: Subscriber недоступен — доставка события этому подписчику не удалась.
    /// Остальные подписчики и Broker продолжают работу (отказ одного Subscriber).
    /// </summary>
    public const string SubscriberUnavailable = "SUBSCRIBER_UNAVAILABLE";

    /// <summary>
    /// ЛР №6: подписка на указанный тип события отсутствует
    /// (Subscriber не зарегистрирован на данный EventType).
    /// </summary>
    public const string NotSubscribed = "NOT_SUBSCRIBED";

    /// <summary>ЛР №6: неизвестный тип события (не входит в предметную область).</summary>
    public const string UnknownEventType = "UNKNOWN_EVENT_TYPE";

    /// <summary>ЛР №6: Subscriber не зарегистрирован в Broker/менеджере Pub/Sub.</summary>
    public const string SubscriberNotRegistered = "SUBSCRIBER_NOT_REGISTERED";

    /// <summary>ЛР №6: ошибка публикации события (Publisher не смог передать событие Broker).</summary>
    public const string PublishError = "PUBLISH_ERROR";
}
