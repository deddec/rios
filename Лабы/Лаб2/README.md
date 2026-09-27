# ЛР №2. Реализация многопоточности и параллельного выполнения в распределённом приложении

**Продолжение ЛР №1. Предметная область (вариант 14): «Заказ и доставка цветов»**

Общий ресурс (вариант 14): **доступный букет** — `Bouquet.AvailableCount`.

> ВАЖНО: проблема **Race Condition** в этой ЛР намеренно НЕ исправляется —
> специально создаётся ситуация конкурентного доступа и показываются её последствия.
> Механизмы синхронизации будут добавлены в ЛР №3.

## Что изменилось по сравнению с ЛР №1 (Требования 1–7)

| Компонент | Изменение |
|---|---|
| `Server/Server.cs` | Приём подключений через `AcceptTcpClientAsync`; **каждый клиент обслуживается в отдельной `Task`** (`Task.Run(HandleClientAsync)`); сервер не блокируется на одном клиенте и продолжает принимать следующие подключения. Добавлены **ID клиентов** (`Client 1`, `Client 2`, …) и **журнал `Journal`** с временными метками START/END, именем операции, ID Task и номером потока |
| `Server/Program.cs` | Асинхронный запуск: `await server.StartAsync()` |
| `Server/RequestHandler.cs` | `HandleAsync` (async/await); поддержка экспериментальной задержки `delayMs` в payload (Требование 4) |
| `Server/BusinessLogic.cs` | `ExecuteAsync` + новые операции: `ReserveBouquet` (окно гонки Race Condition), `SetBouquetAvailability`, `GetBouquetStatus` |
| `Shared/Models/Bouquet.cs` | Поле `AvailableCount` — общий ресурс |
| `Shared/Protocol/MessageFramer.cs` | Async-версии `ReadMessageAsync` / `WriteMessageAsync` |
| `Client/*` | Клиент работает асинхронно (`SendAsync`), печатает метки времени отправки/получения; меню дополнено экспериментами ЛР №2 |

## Архитектура

```
Лаб2/                         ← решение (FlowerShop.sln)
├── Client/
│   ├── Program.cs            ← async-меню, эксперименты ЛР №2, метки времени
│   ├── NetworkClient.cs      ← SendAsync: подключение, отправка, получение ответа
│   └── RequestBuilder.cs     ← формирование JSON-запросов (+ операции ЛР №2)
├── Server/
│   ├── Program.cs            ← точка входа (await server.StartAsync())
│   ├── Server.cs             ← async-приём подключений, Task на клиента,
│   │                           ID клиентов, журнал START/END (Task/Thread ID)
│   ├── RequestHandler.cs     ← HandleAsync + задержка delayMs
│   ├── BusinessLogic.cs      ← 4 операции ЛР №1 + 3 операции ЛР №2
│   └── InMemoryDataStore.cs  ← хранилище + демо-букет «Новогодний люкс» (1 шт.)
├── Shared/                   ← модели и протокол (Request/Response/MessageFramer)
├── Server.Tests/             ← 4 обязательных теста ЛР №2 + тесты ЛР №1
└── README.md
```

## Схема многопоточности (Требования 1–3)

```
AcceptTcpClientAsync ──► Client 1 → Task.Run(HandleClientAsync)  ─┐
       │                     читает запрос → await HandleAsync     │ параллельно
       ├──► Client 2 → Task.Run(HandleClientAsync)                ─┤ (Task/async)
       │                     читает запрос → await HandleAsync     │
       └──► …  сервер продолжает принимать подключения,            ─┘
             не дожидаясь завершения обработки
```

Лог сервера (Требование 3):

```
[10:15:01.120] Client 1 → START      CreateOrder    Task=4 Thread=6
[10:15:01.625] Client 2 → START      CreateOrder    Task=7 Thread=8
[10:15:11.125] Client 1 → END        CreateOrder    Task=4 Thread=6 (10.005 с)
[10:15:11.631] Client 2 → END        CreateOrder    Task=7 Thread=8 (10.006 с)
```

## Эксперимент 1. Доказательство параллельной обработки (Требования 4–5)

Запуск (три терминала, требуется .NET SDK 9.0):

```bash
dotnet build FlowerShop.sln

# терминал 1 — сервер
dotnet run --project Server

# терминал 2 — Client 1: пункт меню 5 (задержка 10 с)
dotnet run --project Client

# терминал 3 — Client 2: пункт меню 5 (задержка 10 с), отправить сразу после Client 1
dotnet run --project Client
```

**Результат:** метки времени показывают, что `Client 2 → START` происходит раньше,
чем `Client 1 → END`, а общее время ≈ 10–11 с вместо ~20 с при последовательной
обработке. Интервалы обработки перекрываются — сервер работает **параллельно**.

Задержка 10 с реализуется полем `delayMs` в payload любого запроса
(обработка: `await Task.Delay(delayMs)` в `RequestHandler.HandleAsync`).

## Эксперимент 2. Race Condition (Требование 6)

Общий ресурс — `Bouquet.AvailableCount` (демо-букет «Новогодний люкс», Id=3, 1 шт.).
Операция `ReserveBouquet` намеренно выполняется в три шага:

```
Шаг 1: READ   available = AvailableCount        ← проверка (оба клиента читают 1)
Шаг 2: DELAY  await Task.Delay(raceDelayMs)     ← окно гонки (по умолчанию 3 с)
Шаг 3: WRITE  AvailableCount--                  ← изменение по УСТАРЕВШЕМУ значению
```

Ход эксперимента:

1. Клиент 1, пункт **7** (SetAvailability): `bouquetId=3`, `count=1` — устанавливаем ресурс.
2. Клиент 1, пункт **6** (ReserveBouquet): `bouquetId=3`.
3. Пока Client 1 в окне гонки (~3 с), клиент 2, пункт **6**: `bouquetId=3`.
4. Клиент 1, пункт **8** (GetBouquetStatus): смотрим состояние ресурса.

**Результат:**

```
[Сервер] [Race Condition] READ: Букет #3 — доступно 1 шт.
[Сервер] [Race Condition] READ: Букет #3 — доступно 1 шт.   ← оба увидели 1
[Сервер] [Race Condition] WRITE: Букет #3 — установлено 0 шт.
[Сервер] [Race Condition] WRITE: Букет #3 — установлено -1 шт.
```

Оба клиента получили **SUCCESS**, хотя ресурс был только один, а `AvailableCount = -1`.
Причина: проверка доступности и её изменение не были атомарны — решение принималось
по устаревшему значению (утерянное обновление). Проблема намеренно оставлена
неисправленной (Важно!!! из методички).

## Новые операции предметной области

| Операция | Назначение |
|---|---|
| `ReserveBouquet` | бронирование букета с окном гонки (эксперимент Race Condition); payload: `bouquetId`, `raceDelayMs?` |
| `SetBouquetAvailability` | установка количества ресурса перед экспериментом; payload: `bouquetId`, `count` |
| `GetBouquetStatus` | состояние общего ресурса; payload: `bouquetId?` |
| `delayMs` (поле любого запроса) | экспериментальная задержка обработки (Требование 4) |

## Тестирование (Требование 10)

```bash
dotnet test FlowerShop.sln
```

Обязательные тесты — `ParallelProcessingTests`:

1. **Один клиент** — `OneClient_RequestIsProcessedCorrectly`: корректная обработка,
   записи START/END в журнале, START ≤ END.
2. **Два клиента** — `TwoClients_BothRequestsAreProcessed`: `Task.WhenAll`,
   оба ответа SUCCESS, разные ClientId, два заказа созданы.
3. **Параллельная обработка** — `TwoClients_WithTenSecondDelay_AreProcessedInParallel`:
   задержка 10 с, Client 2 стартует через 0.5 с; проверяется, что
   `START(Client 2) < END(Client 1)`, интервалы перекрываются и общее время < 19 с
   (последовательно было бы ~20.5 с).
4. **Race Condition** — `TwoClients_ReserveLastBouquet_BothSucceed_RaceConditionOccurs`:
   ресурс = 1, два бронирования в окне гонки; **оба SUCCESS**, `AvailableCount = -1`.

Плюс все прежние тесты ЛР №1 (парсинг, валидация, ошибки, недоступный сервер).

## Как это работает (кратко)

- **Process** — запущенный `dotnet` (сервер или клиент), имеет своё адресное пространство.
- **Thread** — поток внутри процесса; номер потока виден в логе (`Thread=…`).
- **Task** — единица асинхронной работы в C#; сервер оборачивает обработку клиента
  в `Task.Run(() => HandleClientAsync(...))`, а внутри используется `async/await`
  (`Task.Delay`, `ReadMessageAsync`) — поток не блокируется в ожидании.
- **Параллельность** доказывается временными метками START/END и перекрытием интервалов.
- **Race Condition** возникает, когда проверка и изменение общего ресурса не атомарны.
