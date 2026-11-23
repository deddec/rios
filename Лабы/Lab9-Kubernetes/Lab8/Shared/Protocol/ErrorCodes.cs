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

    public const string PrimaryUnavailable = "PRIMARY_UNAVAILABLE";
    public const string ReplicaUnavailable = "REPLICA_UNAVAILABLE";
    public const string AllInstancesUnavailable = "ALL_INSTANCES_UNAVAILABLE";
    public const string ReplicationError = "REPLICATION_ERROR";
    public const string ConsistencyError = "CONSISTENCY_ERROR";
    public const string FailoverError = "FAILOVER_ERROR";

    // ===== ЛР №6: Pub/Sub (Publisher, Broker, Subscriber) =================

    public const string BrokerUnavailable = "BROKER_UNAVAILABLE";
    public const string SubscriberUnavailable = "SUBSCRIBER_UNAVAILABLE";
    public const string NotSubscribed = "NOT_SUBSCRIBED";
    public const string UnknownEventType = "UNKNOWN_EVENT_TYPE";
    public const string SubscriberNotRegistered = "SUBSCRIBER_NOT_REGISTERED";
    public const string PublishError = "PUBLISH_ERROR";

    // ===== ЛР №7: постоянное хранение и гарантированная доставка ==========

    /// <summary>ЛР №7: событие не может быть доставлено ни одному подписчику и перемещено в Dead Letter Queue.</summary>
    public const string DeadLetterQueue = "DEAD_LETTER_QUEUE";

    /// <summary>ЛР №7: подтверждение доставки (ACK) не получено от подписчика в установленное время.</summary>
    public const string AckTimeout = "ACK_TIMEOUT";

    /// <summary>ЛР №7: хранилище событий недоступно.</summary>
    public const string EventStoreUnavailable = "EVENT_STORE_UNAVAILABLE";

    /// <summary>ЛР №7: запрашиваемое событие не найдено в постоянном хранилище.</summary>
    public const string EventNotFound = "EVENT_NOT_FOUND";

    // ===== ЛР №7: выбор лидера и консенсус ================================

    /// <summary>ЛР №7: узел недоступен (не отвечает на сообщения и heartbeat).</summary>
    public const string NodeUnavailable = "NODE_UNAVAILABLE";

    /// <summary>ЛР №7: в кластере нет доступного лидера и выбрать его не удалось.</summary>
    public const string NoLeader = "NO_LEADER";

    /// <summary>ЛР №7: консенсус не достигнут — положительных голосов меньше большинства (quorum).</summary>
    public const string ConsensusNotAchieved = "CONSENSUS_NOT_ACHIEVED";

    /// <summary>ЛР №7: получено некорректное (ошибочное) сообщение от узла.</summary>
    public const string InvalidMessage = "INVALID_MESSAGE";

    /// <summary>ЛР №7: ошибка процедуры выбора лидера.</summary>
    public const string ElectionError = "ELECTION_ERROR";
}
