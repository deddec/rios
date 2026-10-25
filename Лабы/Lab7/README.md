# ЛР №7 — Выбор лидера и консенсус (вариант 14: «Заказ и доставка цветов»)

Продолжение проекта предыдущих ЛР: предметная область (заказ → букет → флорист → доставка, `OrderStatus`)
и протокол «4 байта длины + JSON» сохранены. Новое — кластер из 5 узлов, которые обнаруживают отказы,
выбирают лидера и принимают решения большинством.

## Структура

| Папка | Содержимое |
|---|---|
| `Shared/` | модели, протокол, `EventTypes`/`ShopEvent`, **DTO консенсуса** (`Shared/Consensus`), новые коды ошибок |
| `Server/Consensus/` | **ядро ЛР №7**: `ClusterNode`, `ConsensusCluster`, `ConsensusLog`, сообщения, эксперименты |
| `Server/Services/` | `FlowerOrderService` — заказы меняют статус только после консенсуса |
| `Server/PubSub/` | из ЛР №6/7 (хранилище событий, DLQ, ACK); принятые решения пишутся в `PersistentEventStore` |
| `Server/` | `RequestRouter` (операции протокола), `ConsensusTcpServer`, `Program.cs` |
| `Client/` | консольный клиент |
| `Server.Tests/` | unit-тесты xUnit (77 проверок) |
| `docs/` | материалы для защиты |

## Запуск

```bash
dotnet run --project Server                # сервер на порту 5000, heartbeat каждые 3 с
dotnet run --project Client                # клиент (аргументы: host port)
dotnet run --project Server -- --experiments   # эксперименты 1–5 с печатью журнала, без сети
dotnet test                                # unit-тесты
```

Параметры сервера: `--port`, `--log` (файл журнала, по умолчанию `data/consensus.log`), `--data` (хранилище событий),
`--monitor-ms` (период heartbeat), `--missed` (сколько пропущенных heartbeat считать отказом, по умолчанию 2).

## Как это работает

* **Узел** (`ClusterNode`): ID, `AVAILABLE/FAILED`, `LEADER/FOLLOWER`, список других узлов, реплика состояния
  (версия конфигурации, статусы заказов), приём сообщений `Heartbeat / LeaderAnnounce / Propose / Commit`.
  Отказавший узел молчит (`NO RESPONSE`). Некорректное сообщение → ответ `Error`, исключений нет.
* **Heartbeat** (`HeartbeatRound`): лидер опрашивает последователей, последователи — лидера. После `--missed`
  подряд неотвеченных проверок узел объявляется `FAILED`; если это лидер — автоматически запускаются повторные выборы.
* **Выбор лидера** (`ElectLeader`): инициатор опрашивает все узлы, не ответившие исключаются, лидер — доступный
  узел с максимальным ID. Растёт эпоха (term), остальные получают `LeaderAnnounce` и роль FOLLOWER.
  Одновременно существует один лидер (`LeaderRoleCount`).
* **Консенсус** (`Propose`): лидер рассылает предложение всем узлам; каждый отвечает YES/NO по своим правилам
  (актуальная эпоха и индекс, версия больше текущей, допустимый переход `OrderStatus`). Решение принимается,
  если `YES ≥ Majority = N/2+1`; иначе `CONSENSUS NOT ACHIEVED` и **ничего не применяется**. Если лидер отказал,
  `Propose` сам обнаруживает это и проводит выборы. Все операции кластера сериализуются одной блокировкой.
* **Восстановление** (`Recover`): узел возвращается как FOLLOWER, догоняет состояние лидера; если его ID больше ID
  лидера — проводятся выборы (правило «максимальный ID»).
* **Предметная область**: «создать заказ» = предложение статуса `Created`, «сменить статус» = предложение нового
  статуса. Успешные решения порождают `ShopEvent` (`OrderCreated`, `BouquetPrepared`, `DeliveryArranged`).

## Операции клиента

`GetClusterStatus`, `GetJournal`, `CreateOrder`, `ChangeOrderStatus`, `GetOrder`, `ListOrders`,
`ProposeConfigurationVersion`, `FailNode`, `RecoverNode`, `HeartbeatRound`, `ElectLeader`, `SetDissent`
(узел голосует NO), `RunExperiment`, `RunAllExperiments`, `Ping`.

## Что изменено в присланных файлах

* `Shared/FlowerShop.Shared.csproj` — добавлены `Using` для `System.Text.Json` (без них `Json.cs` и `Request.cs` не компилировались).
* `Shared/PubSub/ShopEvent.cs` — `class` → `record` и `EventId { get; init; }`: `PersistentEventStore` использует `with`,
  а событие должно сохранять свой `EventId` при чтении из файла.
* `Shared/Protocol/ErrorCodes.cs` — добавлены коды `NODE_UNAVAILABLE`, `NO_LEADER`, `CONSENSUS_NOT_ACHIEVED`,
  `INVALID_MESSAGE`, `ELECTION_ERROR`.
