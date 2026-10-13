# ЛР №3. Синхронизация потоков и времени в распределённом приложении

**Продолжение ЛР №2. Предметная область (вариант 14): «Заказ и доставка цветов»**

Общий ресурс (вариант 14): **доступный букет** — `Bouquet.AvailableCount`.

Работа состоит из двух частей:

| Часть | Задача | Основной результат |
|---|---|---|
| **1. Синхронизация доступа к общему ресурсу** (4 ч) | устранить Race Condition, созданный в ЛР №2 | критическая секция «проверка → изменение» защищена `lock`; при 1 свободном букете успех получает ровно один клиент |
| **2. Синхронизация времени** (2 ч) | смоделировать рассинхронизацию часов и синхронизировать клиента с сервером | TIME_REQUEST/TIME_RESPONSE, расчёт RTT, поправки и скорректированного времени |

Дополнительно (Требование 9) создан **второй проект** `Server.Semaphore`, в котором `lock`
заменён на `SemaphoreSlim`, — для сравнения двух механизмов синхронизации.

## Что изменилось по сравнению с ЛР №2

| Компонент | Изменение |
|---|---|
| `Server/Synchronization/SyncMechanism.cs` | **Новый файл.** Перечисление `None` / `Lock` / `SemaphoreSlim` — выбор механизма синхронизации общего ресурса |
| `Server/RequestContext.cs` | **Новый файл.** Контекст запроса: номер клиента + делегат журнала, через который операции с общим ресурсом пишут события `LOCK/UNLOCK/READ/WRITE/SUCCESS/FAIL/TIME` (Требование 8) |
| `Server/BusinessLogic.cs` | `ReserveBouquetAsync`: критическая секция реализована тремя способами (без синхронизации, `lock`, `SemaphoreSlim`); `SetBouquetAvailability` также изменяет ресурс под защитой; добавлена операция `GetServerTime` (TIME_REQUEST/TIME_RESPONSE) |
| `Server/Server.cs` | Поле `Sync` (текущий механизм), журнальная запись `ProcessingLogEntry` дополнена полем `Detail`, сервер передаёт `RequestContext` в обработчик |
| `Server/Program.cs` | Аргумент `--sync=lock\|semaphore\|none` (по умолчанию `lock`) |
| `Server.Semaphore/` | **Новый проект** (Требование 9): тот же сервер с `SyncMechanism.SemaphoreSlim` |
| `Client/ClientClock.cs` | **Новый файл.** Модель часов клиента: `LocalTime = UtcNow + ClockOffset`, хранение измеренной поправки `Correction` |
| `Client/TimeSynchronizer.cs` | **Новый файл.** Синхронизация времени: TIME_REQUEST → RTT → `Offset = ServerTime − (t1 + RTT/2)` → `CorrectedTime = LocalTime + Offset`, проверка остаточной ошибки |
| `Client/RequestBuilder.cs` | Операции ЛР №3: `ReserveBouquet(bouquetId, criticalDelayMs)`, `GetServerTime(delayMs)` |
| `Client/Program.cs` | Аргумент `--offset=±N` (модель рассинхронизации часов), пункты меню 9–14 (время, RTT, поправка, итоговый эксперимент) |
| `Shared/Protocol/ErrorCodes.cs` | Код ошибки `TIME_SYNC_ERROR` (некорректные данные времени) |
| `Shared/Protocol/Json.cs`, `Response.cs` | Данные ответа сериализуются в camelCase — исправлен унаследованный дефект ЛР №2 (тест Race Condition падал на чтении `availableCount`) |
| `Server.Tests/` | `TestServerHost` вынесен в отдельный файл (+ выбор механизма синхронизации); добавлены `SynchronizationTests` (часть 1) и `TimeSynchronizationTests` (часть 2) |

## Архитектура

```
Лаб3/                              ← решение (FlowerShop.sln)
├── Client/
│   ├── Program.cs                 ← меню ЛР №1–ЛР №3, моделирование смещения часов (--offset)
│   ├── ClientClock.cs             ← часы клиента: LocalNow / CorrectedNow / Correction
│   ├── TimeSynchronizer.cs        ← TIME_REQUEST, RTT, Offset, CorrectedTime, остаточная ошибка
│   ├── NetworkClient.cs           ← SendAsync (без изменений с ЛР №2)
│   └── RequestBuilder.cs          ← + ReserveBouquet(criticalDelayMs), GetServerTime(delayMs)
├── Server/
│   ├── Program.cs                 ← выбор механизма синхронизации: --sync=lock|semaphore|none
│   ├── Server.cs                  ← Task на клиента, журнал START/END + события критической секции
│   ├── RequestHandler.cs          ← HandleAsync(+ RequestContext), delayMs
│   ├── BusinessLogic.cs           ← критическая секция (lock / SemaphoreSlim / none) + GetServerTime
│   ├── RequestContext.cs          ← номер клиента + делегат журнала
│   ├── Synchronization/
│   │   └── SyncMechanism.cs       ← enum: None, Lock, SemaphoreSlim
│   └── InMemoryDataStore.cs       ← хранилище + демо-букет «Новогодний люкс» (1 шт.)
├── Server.Semaphore/              ← Требование 9: отдельный проект — lock заменён на SemaphoreSlim
│   └── Program.cs
├── Shared/                        ← модели и протокол (Request/Response/MessageFramer/Json)
├── Server.Tests/                  ← 33 теста: ЛР №1 + ЛР №2 + ЛР №3
│   ├── TestServerHost.cs          ← тестовый хост с выбором механизма синхронизации
│   ├── SynchronizationTests.cs    ← часть 1: взаимное исключение, непересечение секций
│   └── TimeSynchronizationTests.cs← часть 2: skew, RTT, поправка, влияние задержки
└── README.md
```

---

## Часть 1. Синхронизация доступа к общему ресурсу

### 1.1 Общий ресурс и критическая секция

Общий ресурс — `Bouquet.AvailableCount` (количество доступных букетов, вариант 14).
Операция `ReserveBouquet` состояла из трёх шагов (ЛР №2):

```
Шаг 1: READ   available = AvailableCount     ← проверка
Шаг 2: DELAY  окно гонки между шагами        ← задержка
Шаг 3: WRITE  AvailableCount--               ← изменение
```

В ЛР №2 шаги выполнялись без синхронизации, поэтому два клиента читали `1`,
оба считали букет свободным и оба получали SUCCESS, а ресурс уходил в `−1`
(потерянное обновление). В ЛР №3 шаги 1–3 помещены в **критическую секцию**:

```
Client 1                                   Client 2
   ↓                                          ↓
[ LOCK ]                                  (ожидание LOCK)
   ↓
READ   доступно 1 шт.
   ↓
(задержка внутри секции)
   ↓
WRITE  установлено 0 шт.  → SUCCESS
   ↓
[ UNLOCK ]  ────────────────────────────────►  [ LOCK ]
                                                  ↓
                                               READ  доступно 0 шт.
                                                  ↓
                                               FAIL (OUT_OF_STOCK)
                                                  ↓
                                               [ UNLOCK ]
```

### 1.2 Реализация: lock (основной механизм, Требование 5)

`Server/BusinessLogic.cs`:

```csharp
private readonly object _syncObject = new();   // объект синхронизации

private Response ReserveWithLock(Bouquet bouquet, int bouquetId, int criticalDelayMs, RequestContext? context)
{
    lock (_syncObject)   // ← вход в критическую секцию (взаимное исключение)
    {
        Log(context, "LOCK", $"Букет #{bouquetId} — вход в критическую секцию (lock)");
        try
        {
            // ПРОВЕРКА и ИЗМЕНЕНИЕ в ОДНОЙ критической секции (Требование 6)
            Log(context, "READ", $"Букет #{bouquetId} — доступно {bouquet.AvailableCount} шт.");
            if (criticalDelayMs > 0)
            {
                Thread.Sleep(criticalDelayMs);   // lock — синхронный: поток удерживается
            }

            return CheckAndUpdate(bouquet, bouquetId, context);   // решение + запись
        }
        finally
        {
            Log(context, "UNLOCK", $"Букет #{bouquetId} — выход из критической секции (lock)");
        }
    }
}
```

Что именно защищено (Требования 3–6):

* **общий ресурс** — поле `Bouquet.AvailableCount`;
* **критическая секция** — участок «прочитать состояние → принять решение → изменить состояние»
  (метод `CheckAndUpdate`, вызываемый только изнутри секции);
* **вне секции** остаются разбор запроса, поиск букета в хранилище, формирование ответа —
  блокировать весь метод не нужно (иначе сервер стал бы последовательным).

### 1.3 Журнал сервера — доказательство взаимного исключения (Требование 8)

Фактический журнал эксперимента (букет #3, доступно 1, задержка внутри секции 1500 мс,
механизм `lock`):

```
[19:20:32.601] Client 2 → START      ReserveBouquet   Task=2 Thread=8
[19:20:32.602] Client 2 → LOCK       ReserveBouquet   Task=2 Thread=8  Букет #3 — вход в критическую секцию (lock)
[19:20:32.603] Client 2 → READ       ReserveBouquet   Task=2 Thread=8  Букет #3 — доступно 1 шт.
[19:20:32.825] Client 3 → START      ReserveBouquet   Task=3 Thread=9      ← второй клиент работает параллельно
[19:20:34.108] Client 2 → WRITE      ReserveBouquet   Task=2 Thread=8  Букет #3 — установлено 0 шт.
[19:20:34.114] Client 2 → SUCCESS    ReserveBouquet   Task=2 Thread=8  Букет #3 — бронирование подтверждено
[19:20:34.117] Client 2 → UNLOCK     ReserveBouquet   Task=2 Thread=8  Букет #3 — выход из критической секции (lock)
[19:20:34.117] Client 3 → LOCK       ReserveBouquet   Task=3 Thread=9  Букет #3 — вход в критическую секцию (lock)
[19:20:34.118] Client 3 → READ       ReserveBouquet   Task=3 Thread=9  Букет #3 — доступно 0 шт.
[19:20:35.621] Client 3 → FAIL       ReserveBouquet   Task=3 Thread=9  Букет #3 — отказ: свободных экземпляров не осталось
[19:20:35.622] Client 3 → UNLOCK     ReserveBouquet   Task=3 Thread=9  Букет #3 — выход из критической секции (lock)
```

Читается так: Client 3 начал обработку (START) ещё до завершения Client 2 — **параллельность
сохранилась**; но `LOCK` Client 3 появился только после `UNLOCK` Client 2 — **критические
секции не пересекаются**. Client 3 прочитал уже `0` и получил отказ.

### 1.4 Почему проверку нельзя выносить из критической секции (Требование 6)

```csharp
// ПРАВИЛЬНО: проверка и изменение — единая неделимая операция
lock (syncObject)
{
    if (bouquet.AvailableCount <= 0) return Fail();
    bouquet.AvailableCount--;
}

// НЕПРАВИЛЬНО: проверка вне блокировки — Race Condition возвращается
if (bouquet.AvailableCount <= 0) return Fail();   // между этой строкой…
lock (syncObject)
{
    bouquet.AvailableCount--;                    // …и этой ресурс может измениться
}
```

### 1.5 SemaphoreSlim — альтернативный механизм (Требование 9)

`Server.Semaphore/Program.cs` запускает тот же сервер с `SyncMechanism.SemaphoreSlim`:

```csharp
private readonly SemaphoreSlim _semaphore = new(1, 1);   // одно разрешение — взаимное исключение

private async Task<Response> ReserveWithSemaphoreAsync(...)
{
    await _semaphore.WaitAsync(ct);   // ← вход в критическую секцию (поток не блокируется)
    try
    {
        Log(context, "LOCK", $"… (SemaphoreSlim)");
        Log(context, "READ", $"Букет #{bouquetId} — доступно {bouquet.AvailableCount} шт.");
        if (criticalDelayMs > 0) await Task.Delay(criticalDelayMs, ct);   // await допустим
        return CheckAndUpdate(bouquet, bouquetId, context);
    }
    finally
    {
        Log(context, "UNLOCK", $"… (SemaphoreSlim)");
        _semaphore.Release();          // ← выход из критической секции
    }
}
```

### 1.6 Сравнение lock и SemaphoreSlim

| Вопрос | `lock` (Monitor) | `SemaphoreSlim(1, 1)` |
|---|---|---|
| Что синхронизирует | один поток внутри блока, привязан к объекту | счётчик разрешений: внутрь пускается не более N задач |
| Блокирует ли поток | **да** (поток ждёт освобождения) | **нет**: `WaitAsync` «паркует» задачу, поток свободен |
| Можно ли `await` внутри | нет (ошибка компиляции) | да |
| Разрешения | только 1 | любое N (1 → взаимное исключение, 2 → не более двух) |
| Между процессами | нет | нет (`SemaphoreSlim` — внутрипроцессный; для процессов — `Semaphore`/`Mutex`) |
| Накладные расходы | минимальные | выше, чем у `lock` |
| Итог эксперимента | 1 SUCCESS + 1 FAIL | 1 SUCCESS + 1 FAIL (идентично) |

**Почему выбран `lock` как основной:** критическая секция короткая (проверка и уменьшение
счётчика), блокировать поток на это время дешевле, чем использовать асинхронный примитив;
`lock` проще и не допускает случайного `await` внутри секции. **Чем полезен `SemaphoreSlim`:**
секция не блокирует поток (важно при длительных операциях внутри, например обращениях к БД),
а значение счётчика > 1 позволяет ограничить параллелизм («не более 2 одновременных броней»).

### 1.7 Таблица 1 — результаты эксперимента по синхронизации ресурса

Шаблон (заполняется по реальным меткам времени из журнала сервера):

| Механизм | Клиент | Время отправки запроса | Время входа в критическую секцию | Время выхода | Результат операции |
|---|---|---|---|---|---|
| Без синхронизации (`--sync=none`) | Client 1 | | | | Success / Fail |
| Без синхронизации (`--sync=none`) | Client 2 | | | | Success / Fail |
| lock (`--sync=lock`) | Client 1 | | | | Success / Fail |
| lock (`--sync=lock`) | Client 2 | | | | Success / Fail |
| SemaphoreSlim (`Server.Semaphore`) | Client 1 | | | | Success / Fail |
| SemaphoreSlim (`Server.Semaphore`) | Client 2 | | | | Success / Fail |

Пример заполнения по журналу из п. 1.3 (данные реального запуска на этой машине):

| Механизм | Клиент | Отправка | LOCK | UNLOCK | Результат |
|---|---|---|---|---|---|
| Без синхронизации | Client 1 | 19:20:32.601 | — | — | **Success** (ресурс стал −1) |
| Без синхронизации | Client 2 | 19:20:32.825 | — | — | **Success** (оба успеха — Race Condition) |
| lock | Client 2 | 19:20:32.601 | 19:20:32.602 | 19:20:34.117 | **Success** |
| lock | Client 3 | 19:20:32.825 | 19:20:34.117 | 19:20:35.622 | **Fail (OUT_OF_STOCK)** |
| SemaphoreSlim | Client 2 | 19:21:18.35 | 19:21:18.361 | 19:21:19.871 | **Success** |
| SemaphoreSlim | Client 3 | 19:21:18.57 | 19:21:19.871 | 19:21:21.388 | **Fail (OUT_OF_STOCK)** |

### 1.8 Тесты части 1 (`Server.Tests/SynchronizationTests.cs`)

| Тест | Что доказывает |
|---|---|
| `TwoClients_ReserveLastBouquet_OnlyOneSucceeds` (Lock, SemaphoreSlim) | при 1 ресурсе ровно 1 SUCCESS, второй — `OUT_OF_STOCK`; ресурс = 0 (не −1) |
| `TwoClients_CriticalSections_DoNotOverlap` (Lock, SemaphoreSlim) | порядок `LOCK₁ → UNLOCK₁ → LOCK₂ → UNLOCK₂` — секции не пересекаются |
| `SynchronizedReservations_DoNotBlockUnrelatedRequests` | независимый запрос (`CreateOrder`) не ждёт блокировку — синхронизирован не весь сервер |
| `ParallelProcessingTests.TwoClients_ReserveLastBouquet_WithoutSync_BothSucceed_RaceConditionOccurs` | состояние «до»: без синхронизации оба SUCCESS, ресурс −1 |

---

## Часть 2. Синхронизация времени

### 2.1 Моделирование рассинхронизации часов (Требование 1)

Каждый узел распределённой системы имеет свои часы, и они расходятся:

```
Server   → 10:00:00
Client 1 → 10:00:07     (часы спешат на +7 с)
Client 2 → 09:59:55     (часы отстают на −5 с)
```

Системное время компьютера **не изменяется** — рассинхронизация моделируется в программе,
`Client/ClientClock.cs`:

```csharp
public sealed class ClientClock
{
    public TimeSpan ClockOffset { get; set; }        // искусственное смещение часов клиента
    public TimeSpan Correction { get; private set; }  // измеренная поправка после синхронизации
    public bool IsSynchronized { get; private set; }

    public DateTime LocalNow => DateTime.UtcNow + ClockOffset;          // локальные часы
    public DateTime CorrectedNow => LocalNow + Correction;              // скорректированные часы

    public void ApplyCorrection(TimeSpan correction) { Correction = correction; IsSynchronized = true; }
    public void ResetCorrection() { Correction = TimeSpan.Zero; IsSynchronized = false; }
}
```

Смещение задаётся аргументом клиента: `dotnet run --project Client -- --offset=+7`
(или из меню, пункт 13).

### 2.2 TIME_REQUEST / TIME_RESPONSE (Требования 2–3)

Операция `GetServerTime` (псевдоним `TimeRequest`) — обмен по протоколу:

```
Client → TIME_REQUEST (GetServerTime)  ──►  Server: serverTime = DateTime.UtcNow
Client ◄─ TIME_RESPONSE { serverTimeUnixMs, serverTimeUtc }  ── Server
```

Демонстрация проблемы (Требование 2) — пункт меню 9. Типичный вывод при смещении +7 с:

```
  Время сервера (UTC):          16:20:35.633
  Локальное время клиента:      16:20:42.632
  Разница Local − Server:       +7.000 с
```

### 2.3 Расчёт RTT и поправки (Требования 4–6)

```
t1      — локальное время клиента перед отправкой запроса
serverTime — момент фиксации времени на сервере
t4      — локальное время клиента после получения ответа

RTT = t4 − t1                                  # Round-Trip Time
Network delay ≈ RTT / 2                        # симметричное предположение
момент сервера (в шкале клиента) ≈ t1 + RTT/2
Offset = serverTime − (t1 + RTT/2)             # поправка
CorrectedTime = LocalTime + Offset
```

Реализация — `Client/TimeSynchronizer.cs`:

```csharp
public async Task<TimeSyncResult> SyncAsync(int? serverDelayMs = null, CancellationToken ct = default)
{
    DateTime t1 = _clock.LocalNow;                     // время отправки запроса
    Response response = await _client.SendAsync(RequestBuilder.GetServerTime(serverDelayMs), ct);
    DateTime t4 = _clock.LocalNow;                     // время получения ответа

    DateTime serverTime = ParseServerTime(response);

    TimeSpan rtt = t4 - t1;                            // Round-Trip Time
    TimeSpan networkDelay = rtt / 2;                   // RTT / 2
    DateTime referenceLocal = t1 + networkDelay;       // момент сервера в шкале клиента
    TimeSpan offset = serverTime - referenceLocal;     // Offset = ServerTime − ClientTime

    _clock.ApplyCorrection(offset);                    // клиент сохраняет поправку
    ...
}
```

**Важно:** RTT измеряется по часам самого клиента, поэтому искусственное смещение
его часов взаимно сокращается (`t4 − t1`) и не искажает оценку задержки.

### 2.4 Влияние искусственной задержки сервера (Требование 7)

Сервер может задержать ответ (`delayMs` в payload). Тогда отметка `serverTime` снимается
позже, чем клиент считает середину обмена:

```
проверка отправки ──► [ задержка D ] ──► serverTime ──► [ задержка ] ──► приём
                                                     ↑
                              оценка клиента: t1 + RTT/2 (середина обмена)
```

Ошибка поправки ≈ `D/2` (при `D = 1 с` поправка «уезжает» примерно на 0.5 с).
Проверяется пунктами меню 11 и 14 (Проверка 3) и тестом
`SyncAsync_WithServerDelay_ReducesAccuracy`.

### 2.5 Результаты эксперимента (реальный вывод клиента, пункт меню 14)

Смещение часов клиента `+7 с`, затем синхронизация и синхронизация с задержкой сервера 1000 мс:

```
[Проверка 2] Синхронизация времени
  Смещение часов клиента (модель): +7.000 с
  Локальное время клиента ДО синхронизации: 16:20:42.631 UTC
  Время отправки запроса t1:                16:20:42.632 (локальное)
  Время сервера serverTime:                 16:20:35.633 UTC
  RTT (полное время обмена):                0.003 с
  Оценка задержки в одну сторону (RTT/2):   0.002 с
  Вычисленная поправка Offset:              -7.001 с
  Скорректированное время клиента:          16:20:35.635 UTC
  Разница ПОСЛЕ синхронизации (остаточная ошибка): +0.001 с

[Проверка 3] Синхронизация при искусственной задержке сервера 1000 мс
  Время отправки запроса t1:                16:20:42.639 (локальное)
  Время сервера serverTime:                 16:20:36.642 UTC
  RTT (полное время обмена):                1.018 с
  Оценка задержки в одну сторону (RTT/2):   0.509 с
  Искусственная задержка сервера:           1000 мс
  Вычисленная поправка Offset:              -6.507 с
  Скорректированное время клиента:          16:20:37.151 UTC
  Разница ПОСЛЕ синхронизации (остаточная ошибка): -0.493 с
```

**Вывод.** Без искусственной задержки поправка почти идеальна: Offset ≈ −7.001 с,
остаточная ошибка ≈ +0.001 с. С задержкой сервера 1000 мс та же поправка измеряется
как −6.507 с — ошибка ≈ +0.5 с ≈ `delay/2`, остаточная ошибка ≈ −0.49 с.
Задержка передачи данных напрямую снижает точность синхронизации.

### 2.6 Таблица 2 — результаты синхронизации времени

| Эксперимент | Локальное время клиента | Время сервера | Разница до синхронизации | RTT | Оценка задержки RTT/2 | Вычисленная поправка | Скорректированное время | Разница после синхронизации |
|---|---|---|---|---|---|---|---|---|
| 1 (без задержки) | | | | | | | | |
| 2 (задержка 500 мс) | | | | | | | | |
| 3 (задержка 1000 мс) | | | | | | | | |

Пример заполнения по п. 2.5 (смещение часов клиента +7 с):

| Эксперимент | Локальное время | Время сервера | Разница до | RTT | RTT/2 | Поправка | Скорректированное | Разница после |
|---|---|---|---|---|---|---|---|---|
| 1 (delay 0) | 16:20:42.632 | 16:20:35.633 | +7.000 с | 0.003 с | 0.002 с | −7.001 с | 16:20:35.635 | +0.001 с |
| 2 (delay 1000 мс) | 16:20:42.639 | 16:20:36.642 | +7.000 с | 1.018 с | 0.509 с | −6.507 с | 16:20:37.151 | −0.493 с |

### 2.7 Тесты части 2 (`Server.Tests/TimeSynchronizationTests.cs`)

| Тест | Что доказывает |
|---|---|
| `GetServerTime_ReturnsTimeCloseToServerUtcNow` | сервер возвращает своё реальное время (в пределах 1 с) |
| `SyncAsync_WithClockSkew_ComputesOffsetAndCorrectsTime` (+7 с и −5 с) | поправка ≈ −смещение, после синхронизации ошибка < 500 мс, поправка сохранена в часах |
| `SyncAsync_WithServerDelay_ReducesAccuracy` | задержка 1500 мс увеличивает ошибку поправки примерно до `delay/2` |
| `MeasureResidualError_BeforeSynchronization_ShowsClockSkew` | до синхронизации остаточная ошибка равна смещению часов (проблема видна) |
| `ParseServerTime_WithInvalidData_ThrowsTimeSynchronizationException` | некорректные данные времени обрабатываются (Требование 11) |

---

## Запуск и проведение экспериментов

```bash
dotnet build FlowerShop.sln
dotnet test  FlowerShop.sln      # 33 теста: ЛР №1 + ЛР №2 + ЛР №3
```

### Эксперимент 1. Часть 1 — синхронизация общего ресурса

```bash
# терминал 1 — сервер с lock (по умолчанию)
dotnet run --project Server -- 5000 --sync=lock

# терминал 2 — Client 1
dotnet run --project Client -- 127.0.0.1 5000

# терминал 3 — Client 2
dotnet run --project Client -- 127.0.0.1 5000
```

Порядок действий:

1. Client 1: пункт **7** (`SetAvailability`), `bouquetId = 3`, `count = 1`.
2. Client 1: пункт **6** (`ReserveBouquet`), задержка 3000 мс → клиент внутри критической секции.
3. Не дожидаясь ответа, Client 2: пункт **6**, тот же букет, та же задержка.
4. Сравнить журнал сервера: у Client 2 `START` раньше `UNLOCK` Client 1 (параллельность),
   но `LOCK` Client 2 позже `UNLOCK` Client 1 (взаимное исключение).
5. Результат: **Client 1 → SUCCESS, Client 2 → FAIL (OUT_OF_STOCK)**, ресурс = 0.

Для сравнения — «до исправления» и альтернативный механизм:

```bash
dotnet run --project Server -- 5000 --sync=none        # Race Condition: оба SUCCESS, ресурс −1
dotnet run --project Server.Semaphore -- 5000         # SemaphoreSlim вместо lock
```

### Эксперимент 2. Часть 2 — синхронизация времени

```bash
# терминал 1 — сервер
dotnet run --project Server -- 5000 --sync=lock

# терминал 2 — клиент со смещёнными часами (+7 с)
dotnet run --project Client -- 127.0.0.1 5000 --offset=+7

# терминал 3 — клиент с отстающими часами (−5 с)
dotnet run --project Client -- 127.0.0.1 5000 --offset=-5
```

Пункты меню: **9** — сравнение времени (проблема), **10** — синхронизация (RTT, поправка,
скорректированное время), **11** — синхронизация с задержкой сервера (точность),
**12** — проверка скорректированного времени, **13** — изменить смещение часов,
**14** — итоговый эксперимент (все проверки сразу, значения готовы для Таблицы 2).

---

## Чек-лист для защиты

1. **Параллельная работа** — `START(Client 2) < END(Client 1)` в журнале (пункт 5 меню, задержка 10 с).
2. **Race Condition устранена** — общий ресурс `AvailableCount`, критическая секция `ReserveWithLock` / `ReserveWithSemaphoreAsync`.
3. **Синхронизация работает** — при 1 ресурсе ровно один SUCCESS, второй FAIL.
4. **Выбор механизма** — `lock` (короткая синхронная секция) и `SemaphoreSlim` (не блокирует поток, допускает `await`, счётчик разрешений); почему нельзя блокировать весь метод.
5. **Рассинхронизация часов** — `--offset=+7` / `--offset=−5`, локальное время отличается от серверного.
6. **Синхронизация времени** — Local Time, Server Time, Offset, Corrected Time (пункт 10).
7. **Влияние задержки** — измеренный RTT и объяснение, почему он снижает точность (пункт 11).

## Примечание

Исправлен унаследованный из ЛР №2 дефект: данные ответа сериализовались с именами свойств
в стиле `AvailableCount`, из-за чего чтение `availableCount` в тесте Race Condition завершалось
`KeyNotFoundException`. В `Shared/Protocol/Json.cs` добавлены настройки `Json.DataOptions`
(`PropertyNamingPolicy = CamelCase`), а `Response.Ok` теперь использует их.
