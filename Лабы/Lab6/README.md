# ЛР №5. Репликация данных и отказоустойчивость распределённого приложения

**Продолжение ЛР №1–ЛР №4. Предметная область (вариант 14): «Заказ и доставка цветов».**

Основная идея работы — учебная модель **Primary → Replica → Failover → Recovery**
(промышленный кластер СУБД не создаётся): данные предметной области хранятся в двух
независимых экземплярах MSSQL, изменения передаются из основной копии в резервную,
при отказе Primary приложение переключается на Replica, а после восстановления
Primary синхронизируется с актуальными данными.

| Часть | Задача | Основной результат |
|---|---|---|
| **1. Организация Primary и Replica** (1 ч) | создать две независимые копии данных, обеспечить одинаковое начальное состояние | Primary = Replica при старте; состояние каждого экземпляра определяется (AVAILABLE/UNAVAILABLE) |
| **2. Реализация и проверка репликации** (1 ч) | передавать изменения из Primary в Replica | после изменения в Primary копии согласованы; журнал репликации пишется |
| **3. Моделирование отказа и Failover** (1 ч) | смоделировать отказ Primary и переключиться на Replica | при отказе Primary запросы продолжают обрабатываться через Replica |
| **4. Восстановление (Recovery)** (1 ч) | восстановить Primary, синхронизировать данные | после Recovery и синхронизации Primary = Replica; итоговый цикл работает |

Реплицируемый ресурс (вариант 14) — **доступность букета** (`Bouquet.AvailableCount`)
и создаваемые заказы: они изменяются в ходе работы и легко проверяются на совпадение
в двух копиях.

---

## Архитектура

```
┌──────── CLIENT ─────────┐        ┌──────────── SERVER ─────────────┐
│  Меню операций 1–24     │  TCP   │  RequestHandler                 │
│  RequestBuilder         │───────►│  BusinessLogic                  │
│  NetworkClient          │◄───────│    ├─ критическая секция (lock) │
└─────────────────────────┘  JSON  │    └─ ReplicationManager        │
                                   │         ├─ Primary  (FlowerShop)│
                                   │         └─ Replica  (FlowerShopReplica)
                                   └─────────────────────────────────┘
```

* **Primary** — основной экземпляр (`FlowerShop`), все изменения выполняются здесь в штатном режиме.
* **Replica** — резервная копия (`FlowerShopReplica`), получает изменения из Primary.
* **ReplicationManager** — передача изменений, определение состояний, Failover и Recovery.
* Две копии — два независимых соединения с СУБД (один контейнер `mssql/server:2022-latest`,
  две независимые базы данных с одинаковой схемой `Server/Data/schema.sql`).

## Структура проекта

```
Lab5/                                 ← решение (FlowerShop.sln)
├── Client/
│   ├── Program.cs                    ← меню ЛР №1–ЛР №5 (пункты 18–24 — репликация)
│   ├── NetworkClient.cs              ← TCP-клиент
│   ├── RequestBuilder.cs             ← + GetInstanceState, CheckConsistency,
│   │                                     SetInstanceState, Failover, Recover, Synchronize
│   ├── ClientClock.cs, TimeSynchronizer.cs ← ЛР №3 (сохранены)
├── Server/
│   ├── Program.cs                    ← создание Primary и Replica, проверка начального состояния
│   ├── Server.cs                     ← многопоточный TCP-сервер, журнал обработки
│   ├── RequestHandler.cs             ← диспетчер запросов
│   ├── BusinessLogic.cs              ← операции через активный экземпляр + репликация после записи
│   ├── RequestContext.cs             ← номер клиента + делегат журнала
│   ├── Data/
│   │   ├── Database.cs               ← Primary/Replica, EnsureDatabaseExists, ForceUnavailable
│   │   ├── OrderRepository.cs        ← доступ к данным, PlaceOrderAndReserve, TryReserveOne
│   │   └── schema.sql                ← единая схема для обеих копий + демо-данные
│   ├── Replication/
│   │   ├── InstanceState.cs          ← ReplicaRole (Primary/Replica), InstanceState (AVAILABLE/UNAVAILABLE)
│   │   ├── StorageInstance.cs        ← экземпляр хранения + моделирование отказа
│   │   └── ReplicationManager.cs     ← GetActive, Failover, ReplicateAfterChange,
│   │                                     Recover, Synchronize, CheckConsistency
│   └── Synchronization/SyncMechanism.cs ← lock / SemaphoreSlim / none (ЛР №3)
├── Server.Tests/                     ← 58 тестов: ЛР №1–ЛР №5
│   └── ReplicationTests.cs           ← ЛР №5: начальное состояние, репликация,
│                                         отказ, Failover, Recovery, согласованность
├── _smoke/Driver.cs                  ← дымовой прогон полного цикла
└── docs/architecture.svg             ← схема архитектуры
```

---

## Часть 1. Организация основной и резервной копий

При старте сервер (`Server/Program.cs`) выполняет:

1. `EnsureDatabaseExists()` для `FlowerShop` (Primary) и `FlowerShopReplica` (Replica);
2. применяет **одну и ту же** схему `schema.sql` к обеим базам (гарантия одинаковой
   структуры — Требование 4);
3. проверяет доступность обоих экземпляров и их **начальное совпадение**
   (Требование 7: `CheckConsistency()` при старте).

```
[Сервер] ЛР №5: инициализация Primary и Replica...
[Сервер] БД 'FlowerShop' доступна ✓
[Сервер] БД 'FlowerShopReplica' доступна ✓
[Сервер] Схема Primary и Replica инициализирована ✓
[Сервер] Начальное состояние: Primary = Replica ✓
[Сервер] Primary=AVAILABLE (FlowerShop), Replica=AVAILABLE (FlowerShopReplica), активный=Primary
```

Состояние каждого экземпляра — `AVAILABLE` / `UNAVAILABLE` (`InstanceState`),
определяется активной проверкой (`CheckAvailability` → `TryPing`).

### Передача изменений (Требование 6)

Любая операция изменения (`CreateOrder`, `ReserveBouquet`, `SetBouquetAvailability`,
`PlaceOrderAndReserve`) выполняется в **активном** экземпляре, а после успешной
записи `ReplicationManager.ReplicateAfterChange` переносит реплицируемые данные
(букеты + заказы, с сохранением идентификаторов) в резервную копию в одной
транзакции. Если резервный экземпляр недоступен — репликация откладывается
(изменения копятся и синхронизируются при Recovery), приложение не падает.

---

## Часть 2. Репликация: журнал и таблица результатов

Изменение в Primary и передача в Replica (фрагмент журнала сервера, реальный запуск):

```
[19:03:58.971] Client 2 → START      SetBouquetAvailability Task=- Thread=9
[19:03:58.983] Client 2 → WRITE      SetBouquetAvailability  SetBouquetAvailability Букет #2 — AvailableCount = 5 (БД)
[19:03:58.984] Client 2 → REPLICATION SetBouquetAvailability  Replication → START: передача изменений Primary → Replica
[19:03:59.017] Client 2 → REPLICATION SetBouquetAvailability  Replication → SUCCESS: Primary = Replica (данные согласованы)
[19:03:59.020] Client 2 → END        SetBouquetAvailability (0,049 с)
```

### Таблица результатов репликации

| Эксперимент | Состояние Primary | Состояние Replica | Результат | Комментарий |
|---|---|---|---|---|
| Начальное состояние (старт сервера) | AVAILABLE | AVAILABLE | Primary = Replica ✓ | одинаковая схема + демо-данные в обеих базах |
| Изменение: `SetAvailability` букета #2 → 5 | AVAILABLE | AVAILABLE | Primary = Replica ✓ | `WRITE` в Primary, затем `REPLICATION → SUCCESS` |
| Три последовательных изменения | AVAILABLE | AVAILABLE | Primary = Replica ✓ | после каждого изменения копии совпадают (тест `Replication_ThreeSequentialChanges_AlwaysConsistent`) |
| `PlaceOrderAndReserve` (заказ + бронь в транзакции) | AVAILABLE | AVAILABLE | Primary = Replica ✓ | заказ и уменьшение ресурса реплицированы (тест `PlaceOrderAndReserve_ReplicatesToReplica`) |
| Replica недоступен при изменении | AVAILABLE | UNAVAILABLE | изменения сохранены в Primary | `REPLICATION_SKIPPED`: репликация отложена до Recovery |

---

## Часть 3. Моделирование отказа и Failover

Отказ моделируется операцией `SetInstanceState` (Primary → UNAVAILABLE): соединение
с базой блокируется (`Database.ForceUnavailable`), любая операция с экземпляром
завершается ошибкой — приложение обязано это обнаружить и переключиться.

```
[19:03:59.040] Client 4 → START      SetInstanceState
[Репликация] STATE: Primary → UNAVAILABLE (смоделировано клиентом/тестом)
[19:03:59.045] Client 5 → START      Failover
[19:03:59.046] Client 5 → FAILOVER   Failover  Failover выполнен: Primary → Replica. Запросы обслуживаются через Replica (FlowerShopReplica).
[19:03:59.049] Client 6 → START      GetBouquetStatus      ← запрос уже через Replica
```

**Автоматическое переключение**: даже без явного `Failover` следующий запрос через
`ReplicationManager.GetActive()` обнаруживает недоступность активного экземпляра,
логирует `FAILOVER` и обслуживает запрос через Replica. Если недоступны **оба**
экземпляра — клиент получает понятную ошибку `ALL_INSTANCES_UNAVAILABLE`.

### Таблица результатов отказа

| Проверка | Результат |
|---|---|
| Первичное состояние до отказа | Primary=AVAILABLE, Replica=AVAILABLE, активный=Primary |
| Искусственное отключение Primary | Primary → UNAVAILABLE, Replica → AVAILABLE |
| Обнаружение отказа | отказ зафиксирован в журнале (`STATE`, `FAILOVER`) |
| Переключение на Replica | Failover выполнен: активный экземпляр — Replica |
| Обработка запросов после переключения | чтение и изменение успешно обслуживаются через Replica (`GetBouquetStatus`, `SetBouquetAvailability` — SUCCESS) |
| Запрос при отказе обоих экземпляров | ошибка `ALL_INSTANCES_UNAVAILABLE` с описанием состояний (тест `BothInstancesUnavailable_ReturnsError`) |

---

## Часть 4. Восстановление (Recovery) и синхронизация

После восстановления Primary его данные могут отставать (во время отказа изменения
делались через Replica). `Recover` возвращает экземпляр в строй, синхронизирует
его с актуальным (активным) экземпляром и делает активным снова Primary:

```
[19:03:59.054] Client 7 → START      Recover
[19:03:59.055] Client 7 → RECOVERY   Recover  Recovery → START: восстановление Primary
[19:03:59.055] Client 7 → SYNCHRONIZATION Recover  Синхронизация после восстановления: Replica → Primary
[19:03:59.075] Client 7 → RECOVERY   Recover  Recovery → SUCCESS: Primary восстановлен и синхронизирован. Активный экземпляр снова Primary.
[19:03:59.084] Client 7 → RECOVERY   Recover  Проверка после восстановления: Primary = Replica (согласованы)
```

### Таблица итогового цикла (пункт меню 24 / дымовой драйвер `_smoke`)

| Этап | Состояние системы | Согласованность данных | Результат |
|---|---|---|---|
| 1. Штатное состояние | Primary=AVAILABLE, Replica=AVAILABLE | Primary = Replica ✓ | `GetInstanceState`: согласованы |
| 2. Изменение в Primary → репликация | активен Primary | Primary = Replica ✓ | `SetBouquetAvailability` → `REPLICATION → SUCCESS` |
| 3. Отказ Primary | Primary=UNAVAILABLE, Replica=AVAILABLE | копии могут расходиться | `SetInstanceState`: отказ смоделирован, зафиксирован |
| 4. Failover и работа через Replica | активен Replica | изменение накапливается в Replica | `Failover` → SUCCESS; `GetBouquetStatus` через Replica → SUCCESS |
| 5. Recovery + синхронизация | Primary=AVAILABLE, активен Primary | Primary = Replica ✓ | `Recover` → SUCCESS; `CheckConsistency`: согласованы |

Полный цикл подтверждён дымовым прогоном (`_smoke/Driver.cs`) — все 8 операций
вернули `success=True`, журнал сохранён в `server_smoke.log`.

| Проверка | Результат |
|---|---|
| Первичное состояние до отказа | Primary=AVAILABLE, Replica=AVAILABLE, активный=Primary |
| Искусственное отключение Primary | Primary → UNAVAILABLE, Replica → AVAILABLE |
| Обнаружение отказа | отказ зафиксирован в журнале (`STATE`, `FAILOVER`) |
| Переключение на Replica | Failover выполнен: активный экземпляр — Replica |
| Обработка запросов после переключения | чтение и изменение успешно обслуживаются через Replica (`GetBouquetStatus`, `SetBouquetAvailability` — SUCCESS) |
| Запрос при отказе обоих экземпляров | ошибка `ALL_INSTANCES_UNAVAILABLE` с описанием состояний (тест `BothInstancesUnavailable_ReturnsError`) |

---

## Часть 4. Восстановление (Recovery) и синхронизация

После восстановления Primary его данные могут отставать (во время отказа изменения
делались через Replica). `Recover` возвращает экземпляр в строй, синхронизирует
его с актуальным (активным) экземпляром и делает активным снова Primary:

```
[19:03:59.054] Client 7 → START      Recover
[19:03:59.055] Client 7 → RECOVERY   Recover  Recovery → START: восстановление Primary
[19:03:59.055] Client 7 → SYNCHRONIZATION Recover  Синхронизация после восстановления: Replica → Primary
[19:03:59.075] Client 7 → RECOVERY   Recover  Recovery → SUCCESS: Primary восстановлен и синхронизирован. Активный экземпляр снова Primary.
[19:03:59.084] Client 7 → RECOVERY   Recover  Проверка после восстановления: Primary = Replica (согласованы)
```

### Таблица итогового цикла (пункт меню 24 / дымовой драйвер `_smoke`)

| Этап | Состояние системы | Согласованность данных | Результат |
|---|---|---|---|
| 1. Штатное состояние | Primary=AVAILABLE, Replica=AVAILABLE | Primary = Replica ✓ | `GetInstanceState`: согласованы |
| 2. Изменение в Primary → репликация | активен Primary | Primary = Replica ✓ | `SetBouquetAvailability` → `REPLICATION → SUCCESS` |
| 3. Отказ Primary | Primary=UNAVAILABLE, Replica=AVAILABLE | копии могут расходиться | `SetInstanceState`: отказ смоделирован, зафиксирован |
| 4. Failover и работа через Replica | активен Replica | изменение накапливается в Replica | `Failover` → SUCCESS; `GetBouquetStatus` через Replica → SUCCESS |
| 5. Recovery + синхронизация | Primary=AVAILABLE, активен Primary | Primary = Replica ✓ | `Recover` → SUCCESS; `CheckConsistency`: согласованы |

Полный цикл подтверждён дымовым прогоном (`_smoke/Driver.cs`) — все 8 операций
вернули `success=True`, журнал сохранён в `server_smoke.log`.


