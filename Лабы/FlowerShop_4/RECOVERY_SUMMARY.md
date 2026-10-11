# 📋 Отчет о восстановлении проекта FlowerShop ЛР №4

**Дата:** 11 октября 2026  
**Время:** 15:03 МСК  
**Сессия:** https://claude.ai/code/session_014SYqBCPTnGFeDmcGDKgoKc

---

## ✅ Что было сделано

### 1️⃣ Восстановлен файл `Server/Data/OrderRepository.cs`

**Проблема:** Файл был поврежден, остался только record, 326 байт

**Решение:** Полное восстановление класса OrderRepository с 10 методами:

#### Основные методы CRUD:
```csharp
// Чтение
public List<Order> ListOrders()
public Order? GetOrder(int orderId)
public bool Exists(int orderId)
public List<Bouquet> ListBouquets()
public int GetAvailableCount(int bouquetId)

// Создание и изменение
public int CreateOrder(...)
public void SetAvailableCount(int bouquetId, int count)
public bool TryReserveOne(int bouquetId)
public void AssignFlorist(int orderId, int floristId)
```

#### Ключевая операция ЛР №4:
```csharp
public PlaceOrderOutcome PlaceOrderAndReserve(
    int customerId,
    int bouquetId,
    string deliveryAddress,
    DateTime deliveryDate,
    bool useTransaction = true,
    bool simulatedError = false)
```

**Особенности:**
- ✅ Поддержка транзакций (BEGIN TRANSACTION → COMMIT/ROLLBACK)
- ✅ Изоляция уровня ReadCommitted
- ✅ Имитация ошибок для демонстрации ROLLBACK
- ✅ Обработка исключений БД
- ✅ Вспомогательный метод `GetFlowerItemsInTransaction`

---

### 2️⃣ Обновлен файл `Server/Program.cs`

**Проблема:** Не было инициализации БД и OrderRepository

**Решение:** Добавлена полная инициализация:

```csharp
// Инициализация Database
var database = new Database();

// Проверка доступности (Требование 8)
if (!database.TryPing(out string dbError)) { ... }

// Создание схемы из schema.sql
database.EnsureSchema(schemaPath);

// Инициализация репозитория
var orderRepository = new OrderRepository(database);
```

**Добавлены параметры командной строки:**
- `--sync=lock` (основной)
- `--sync=semaphore` (альтернативный)
- `--sync=none` (для сравнения)
- `--skip-db-init` (пропустить инициализацию)

---

### 3️⃣ Обновлен файл `Server/FlowerShop.Server.csproj`

**Добавлена зависимость:**
```xml
<PackageReference Include="Microsoft.Data.SqlClient" Version="5.1.5" />
```

---

## 📊 Статус компонентов

| Компонент | Статус | Примечание |
|-----------|--------|-----------|
| `Database.cs` | ✅ OK | Подключение к MSSQL, обработка ошибок |
| `OrderRepository.cs` | ✅ ВОССТАНОВЛЕН | Все 10 методов реализованы |
| `Program.cs` | ✅ ОБНОВЛЕН | Инициализация БД и репозитория |
| `Server.csproj` | ✅ ОБНОВЛЕН | Добавлен Microsoft.Data.SqlClient |
| `schema.sql` | ✅ OK | 7 таблиц, данные, ограничения |
| `BusinessLogic.cs` | ✅ OK | Синхронизация (lock, SemaphoreSlim) |
| `Server.cs` | ✅ OK | Многопоточность, журнал |
| `Shared/*` | ✅ OK | Модели и протоколы |
| `Server.Tests/*` | ✅ OK | 8 тестовых файлов |

---

## 🏗️ Архитектура ЛР №4

```
Клиент (Client)
    ↓ TCP
Сервер (Server.cs)
    ↓ JSON Request/Response
Обработчик (RequestHandler.cs)
    ↓
Логика (BusinessLogic.cs)
    ↓ Доступ к данным
Репозиторий (OrderRepository.cs)
    ↓ SQL + Транзакции
MSSQL (Database.cs)
```

### Трехуровневая архитектура:
1. **Presentation Layer:** RequestHandler
2. **Business Logic Layer:** BusinessLogic
3. **Data Access Layer:** OrderRepository + Database

**Принцип:** Клиент не имеет прямого доступа к БД, все операции проходят через Server → BusinessLogic → OrderRepository → Database.

---

## 🔐 Транзакции (ЛР №4 ключевая особенность)

### PlaceOrderAndReserve демонстрирует:

**Сценарий 1: Успешная транзакция (useTransaction=true)**
```
BEGIN TRANSACTION
  ├─ Получить AvailableCount (до)
  ├─ Проверить наличие
  ├─ INSERT заказ
  ├─ UPDATE Bouquets (AvailableCount--)
  └─ Получить AvailableCount (после)
COMMIT
```
✅ Результат: оба изменения сохранены

**Сценарий 2: Откат при ошибке (simulatedError=true)**
```
BEGIN TRANSACTION
  ├─ Получить AvailableCount (до)
  ├─ Проверить наличие
  ├─ INSERT заказ
  ├─ ❌ SIMULATED ERROR
ROLLBACK
```
✅ Результат: БД вернулась в исходное состояние

**Сценарий 3: Без транзакции (useTransaction=false)**
```
-- Автокоммит каждой операции
├─ Получить AvailableCount (до)
├─ Проверить наличие
├─ INSERT заказ (COMMIT автоматический)
├─ UPDATE Bouquets (COMMIT автоматический)
└─ Получить AvailableCount (после)
```
⚠️ Результат: Если ошибка между операциями, БД в несогласованном состоянии

---

## 🧪 Требования ЛР №4

### Требование 1: Архитектура Client → Server → Database
✅ Клиент отправляет TCP запросы серверу  
✅ Сервер обслуживает в отдельной Task (асинхронно)  
✅ OrderRepository обращается к MSSQL  

### Требование 2: Асинхронная обработка клиентов
✅ Server.StartAsync() ждет подключений в цикле  
✅ Каждый клиент → отдельная Task (Task.Run)  
✅ Сервер не блокируется на одном клиенте  

### Требование 3: Обработка ошибок подключения
✅ Database.Open() бросает OperationException  
✅ Program.cs проверяет TryPing() перед стартом  
✅ Сервер не стартует без БД  

### Требование 4: CRUD операции
✅ Create: CreateOrder()  
✅ Read: GetOrder(), ListOrders(), ListBouquets()  
✅ Update: SetAvailableCount(), AssignFlorist()  
✅ Delete: not explicitly required, but cascading deletes in schema  

### Требование 5: Синхронизация (lock)
✅ Из ЛР №3, реализовано в BusinessLogic.cs  
✅ Механизм: lock(_syncObject) для críritical section  

### Требование 6: Данные в БД вместо памяти
✅ OrderRepository работает с SQL, не с InMemoryDataStore  
✅ Schema содержит 7 таблиц  

### Требование 7: Параллельный доступ нескольких клиентов
✅ Server.HandleClientAsync() в отдельных Task'ах  
✅ Журнал (Server.Journal) фиксирует параллельность  

### Требование 8: Обработка ошибок БД
✅ Database.TryPing() для проверки  
✅ Database.Open() с обработкой SqlException  
✅ Program.cs выводит сообщение об ошибке при недоступности БД  

### Требование 9: Транзакции с COMMIT/ROLLBACK
✅ PlaceOrderAndReserve() использует SqlTransaction  
✅ BeginTransaction(IsolationLevel.ReadCommitted)  
✅ Поддержка rollback при ошибке или simulatedError=true  

### Требование 10: Демонстрация согласованности
✅ PlaceOrderOutcome показывает AvailableBefore/After  
✅ Сценарий "симулированная ошибка" показывает rollback  
✅ Журнал Server.Journal показывает параллельные операции  

---

## 🚀 Как использовать восстановленный проект

### Шаг 1: Запустить MSSQL контейнер
```bash
docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=MyStrongPassword" \
  -p 1433:1433 \
  --name flowershop-db \
  mssql/server:2022-latest
```

### Шаг 2: Запустить сервер
```bash
dotnet run --project Server -- --sync=lock
```

### Шаг 3: Запустить клиент (в другом терминале)
```bash
dotnet run --project Client
```

### Шаг 4: Запустить тесты
```bash
dotnet test
```

---

## 📝 Ключевые файлы

### Восстановленные:
- ✅ `Server/Data/OrderRepository.cs` (700+ строк, полностью восстановлен)

### Обновленные:
- ✅ `Server/Program.cs` (добавлена инициализация БД)
- ✅ `Server/FlowerShop.Server.csproj` (добавлена зависимость)

### Не требовали изменений:
- `Server/Data/Database.cs` (уже готов)
- `Server/Data/schema.sql` (уже готов)
- `Server/BusinessLogic.cs` (уже готов)
- `Server/Server.cs` (уже готов)
- `Shared/*` (уже готов)

---

## 🎯 Результат

### До восстановления:
❌ OrderRepository.cs поврежден (326 байт)  
❌ Нет инициализации БД в Program.cs  
❌ Build не проходит (CS0234/CS0246)  

### После восстановления:
✅ OrderRepository.cs полностью восстановлен  
✅ Program.cs инициализирует БД и репозиторий  
✅ Все 10 методов реализованы  
✅ Транзакции полностью поддерживаются  
✅ Обработка ошибок на месте  
✅ Project готов к запуску и тестированию  

---

## 📌 Информация для ЛР

**Вариант:** 14 — «Заказ и доставка цветов»  
**Тема:** Распределенная работа с БД и транзакции  
**Цель:** Изучить принципы работы многоуровневого приложения с MSSQL  

**Основной результат:** PlaceOrderAndReserve демонстрирует:
- Создание заказа и резервирование букета как единую неделимую операцию
- COMMIT при успехе
- ROLLBACK при ошибке (сохраняет консистентность БД)

---

**Проект готов к тестированию! ✅**

