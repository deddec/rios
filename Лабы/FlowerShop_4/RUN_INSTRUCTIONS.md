# ЛР №4: Запуск и тестирование проекта FlowerShop

## 1. Требования

- .NET 9.0 SDK
- Docker (для запуска MSSQL контейнера)
- VS Code или Visual Studio

## 2. Подготовка: запуск MSSQL контейнера

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=MyStrongPassword123!" -p 1433:1433 --name flowershop-db --hostname flowershop-db -d mcr.microsoft.com/mssql/server:2022-latest
docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=MyStrongPassword" \
  -p 1433:1433 \
  --name flowershop-db \
  mssql/server:2022-latest
```

Проверка подключения:

```bash
sqlcmd -S localhost,1433 -U sa -P MyStrongPassword123! -Q "SELECT @@VERSION"
```

## 3. Запуск сервера

```bash
# Основной вариант с lock (Требование 5)
dotnet run --project Server -- --sync=lock

# Альтернативный вариант с SemaphoreSlim (Требование 9)
dotnet run --project Server -- --sync=semaphore

# Режим без синхронизации (для сравнения, ЛР №2)
dotnet run --project Server -- --sync=none

# Пропустить инициализацию схемы (если уже инициализирована)
dotnet run --project Server -- --sync=lock --skip-db-init
```

Сервер должен вывести:

```
[Сервер] ЛР №4: инициализация подключения к MSSQL...
[Сервер] БД доступна ✓
[Сервер] Создание схемы БД...
[Сервер] Схема БД инициализирована ✓
[Сервер] Репозиторий заказов инициализирован ✓
[Сервер] ЛР №4: механизм синхронизации общего ресурса — lock
[Сервер] Сервер запущен на порту 5000
```

## 4. Запуск тестов

```bash
# Все тесты
dotnet test

# Конкретный тест класс
dotnet test --filter "ClassName=SynchronizationTests"

# Тест с выводом информации
dotnet test -v n
```

## 5. Основные улучшения в ЛР №4

### OrderRepository.cs (восстановлен)

- ✅ `ListOrders()` - получить все заказы из БД
- ✅ `GetOrder(int orderId)` - получить заказ по Id
- ✅ `Exists(int orderId)` - проверить существование
- ✅ `ListBouquets()` - получить букеты с цветами
- ✅ `GetAvailableCount(int bouquetId)` - количество доступных
- ✅ `CreateOrder(...)` - создать заказ
- ✅ `SetAvailableCount(int bouquetId, int count)` - установить количество
- ✅ `TryReserveOne(int bouquetId)` - попытка резервирования
- ✅ `AssignFlorist(int orderId, int floristId)` - назначить флориста
- ✅ `PlaceOrderAndReserve(...)` - КЛЮЧЕВАЯ ОПЕРАЦИЯ: создать заказ + зарезервировать в одной транзакции

### Program.cs (обновлен)

- ✅ Инициализация Database
- ✅ Проверка доступности БД (Требование 8)
- ✅ Создание схемы из schema.sql
- ✅ Инициализация OrderRepository
- ✅ Добавлены параметры --skip-db-init для опцион инициализации

### Database.cs (уже был)

- ✅ Подключение к MSSQL (localhost:1433)
- ✅ Обработка ошибок подключения
- ✅ Выполнение schema.sql с разбиением по GO

### schema.sql (уже был)

- ✅ 7 таблиц (Customers, Bouquets, FlowerItems, Florists, Orders, Deliveries, OrderEvents)
- ✅ Первичные и внешние ключи
- ✅ Ограничения (CHECK, UNIQUE)
- ✅ Демонстрационные данные

## 6. Экспериментальные сценарии

### Сценарий 1: PlaceOrderAndReserve с транзакцией

```json
{
  "operation": "PlaceOrderAndReserve",
  "payload": {
    "customerId": 1,
    "bouquetId": 1,
    "deliveryAddress": "ул. Ленина, 10",
    "deliveryDate": "2026-10-15",
    "useTransaction": true,
    "simulatedError": false
  }
}
```

Результат: заказ создан, букет зарезервирован, БД в консистентном состоянии.

### Сценарий 2: PlaceOrderAndReserve с имитацией ошибки

```json
{
  "operation": "PlaceOrderAndReserve",
  "payload": {
    "customerId": 1,
    "bouquetId": 1,
    "deliveryAddress": "ул. Ленина, 10",
    "deliveryDate": "2026-10-15",
    "useTransaction": true,
    "simulatedError": true
  }
}
```

Результат: транзакция откатывается, ни заказ, ни букет не изменяются (ROLLBACK).

### Сценарий 3: PlaceOrderAndReserve БЕЗ транзакции

```json
{
  "operation": "PlaceOrderAndReserve",
  "payload": {
    "customerId": 1,
    "bouquetId": 1,
    "deliveryAddress": "ул. Ленина, 10",
    "deliveryDate": "2026-10-15",
    "useTransaction": false,
    "simulatedError": false
  }
}
```

Результат: операции выполняются с автокоммитом (для сравнения эффективности).

## 7. Файлы проекта

```
FlowerShop.sln
├── Server/
│   ├── Program.cs (обновлен)
│   ├── Server.cs
│   ├── BusinessLogic.cs
│   ├── RequestHandler.cs
│   ├── RequestContext.cs
│   ├── InMemoryDataStore.cs
│   ├── Data/
│   │   ├── Database.cs (существует)
│   │   ├── OrderRepository.cs (восстановлен ✓)
│   │   └── schema.sql (существует)
│   ├── Synchronization/
│   │   └── SyncMechanism.cs
│   └── FlowerShop.Server.csproj (обновлен)
├── Server.Tests/
│   ├── BusinessLogicTests.cs
│   ├── ParallelProcessingTests.cs
│   ├── SynchronizationTests.cs
│   ├── TimeSynchronizationTests.cs
│   └── ...
├── Shared/
│   ├── Models/
│   │   ├── Customer.cs
│   │   ├── Bouquet.cs
│   │   ├── Order.cs
│   │   ├── Florist.cs
│   │   └── ...
│   └── Protocol/
│       ├── Request.cs
│       ├── Response.cs
│       ├── ErrorCodes.cs
│       └── ...
└── Client/
    └── ...
```

## 8. Ключевые классы

### PlaceOrderOutcome (record)

Результат операции PlaceOrderAndReserve:

- `OrderId` - ID созданного заказа
- `BouquetId` - ID букета
- `AvailableBefore` - количество доступных до операции
- `AvailableAfter` - количество доступных после операции
- `UseTransaction` - была ли использована транзакция
- `SimulatedError` - была ли имитирована ошибка

### OrderRepository (class)

Репозиторий для работы с заказами и букетами в MSSQL.
Поддерживает CRUD-операции и транзакции.

## 9. Требования ЛР №4

- ✅ Требование 1: Клиент не обращается к БД напрямую
- ✅ Требование 2: Клиент → Сервер → БД (трехуровневая архитектура)
- ✅ Требование 3: Data Access Layer (OrderRepository)
- ✅ Требование 4: CRUD-операции для заказов
- ✅ Требование 5: Синхронизация доступа (lock) - из ЛР №3
- ✅ Требование 6: Хранение в БД вместо памяти
- ✅ Требование 7: Параллельный доступ нескольких клиентов
- ✅ Требование 8: Обработка ошибок подключения к БД
- ✅ Требование 9: Транзакции MSSQL (BEGIN/COMMIT/ROLLBACK)
- ✅ Требование 10: Демонстрация согласованности данных
