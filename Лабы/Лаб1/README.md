# ЛР №1. Разработка клиент-серверного приложения

**Предметная область (вариант 14): «Заказ и доставка цветов»**

- **Сущности:** Customer, Bouquet, Order, Florist, Delivery
- **Операции:** CreateOrder, CustomizeBouquet, AssignFlorist, ArrangeDelivery
- **События:** OrderCreated, BouquetPrepared, FloristAssigned, DeliveryArranged

Приложение на **C# (.NET 9)**, построенное по клиент-серверной архитектуре:
клиент формирует JSON-запросы и отправляет их по TCP серверу; сервер определяет
операцию, выполняет бизнес-логику и возвращает JSON-ответ.

## Архитектура

```
FlowerShop/                     ← решение (FlowerShop.sln)
├── Client/                     ← клиентская часть
│   ├── Program.cs              ← меню операций, ввод данных, вывод результатов
│   ├── NetworkClient.cs        ← подключение к серверу, отправка/получение сообщений
│   └── RequestBuilder.cs       ← формирование JSON-запросов
├── Server/                     ← серверная часть
│   ├── Program.cs              ← точка входа сервера
│   ├── Server.cs               ← приём подключений (TcpListener, поток на клиента)
│   ├── RequestHandler.cs       ← разбор запроса, определение операции, ошибки
│   ├── BusinessLogic.cs        ← бизнес-логика четырёх операций
│   └── InMemoryDataStore.cs    ← временное хранилище данных (коллекции)
├── Shared/                     ← общая библиотека
│   ├── Models/                 ← Customer, Bouquet, Order, Florist, Delivery
│   └── Protocol/               ← Request, Response, ErrorCodes, MessageFramer, Json
├── Server.Tests/               ← unit-тесты (xUnit)
├── docs/
│   └── architecture.png        ← схема архитектуры
└── README.md
```

## Протокол обмена

Транспорт: **TCP** (`System.Net.Sockets`). Формат сообщения: **JSON**.

Кадрирование в потоке: `[4 байта длины (little-endian)][UTF-8 JSON]`
(класс `MessageFramer`). Сериализация — `System.Text.Json`.

**Запрос клиента:**

```json
{
  "operation": "CreateOrder",
  "payload": {
    "customerId": 1,
    "bouquetId": 1,
    "deliveryAddress": "ул. Ленина, 10",
    "deliveryDate": "2026-09-30"
  }
}
```

**Успешный ответ сервера:**

```json
{
  "success": true,
  "errorCode": null,
  "message": "Заказ #1 создан для Иванова Анна. Сумма: 1080 руб.",
  "data": { "id": 1, "status": "Created", "bouquet": "Розовое облако", "total": 1080 }
}
```

**Ответ с ошибкой:**

```json
{
  "success": false,
  "errorCode": "NOT_FOUND",
  "message": "Букет с Id=5 не найден",
  "data": null
}
```

Коды ошибок: `SERVER_UNAVAILABLE`, `UNKNOWN_OPERATION`, `NOT_FOUND`,
`VALIDATION_ERROR`, `NETWORK_ERROR`, `INTERNAL_ERROR`.

## Операции предметной области

| Операция | Входные данные | Результат |
|---|---|---|
| `CreateOrder` | customerId, bouquetId, deliveryAddress, deliveryDate | создаёт заказ со статусом `Created`, событие `OrderCreated` |
| `CustomizeBouquet` | orderId, name?, flowers? | меняет состав/название букета, статус `BouquetPrepared`, событие `BouquetPrepared` |
| `AssignFlorist` | orderId, floristId | назначает свободного флориста, статус `FloristAssigned`, событие `FloristAssigned` |
| `ArrangeDelivery` | orderId, address?, plannedAt? | создаёт доставку, статус `DeliveryArranged`, событие `DeliveryArranged` |

## Обработка ошибок (Требование 8)

| Ошибка | Как проявляется |
|---|---|
| Сервер недоступен | клиент не может подключиться → `ServerUnavailableException`, понятное сообщение |
| Некорректная операция | сервер возвращает `UNKNOWN_OPERATION` с перечнем доступных операций |
| Отсутствующий объект | сервер возвращает `NOT_FOUND` (клиент/букет/заказ/флорист не найден) |
| Некорректные входные данные | сервер возвращает `VALIDATION_ERROR` с указанием проблемного поля |
| Ошибка сетевого взаимодействия | перехват `IOException`/`SocketException` на обеих сторонах |

## Запуск

Требуется .NET SDK 9.0.

```bash
# 1. Сборка
dotnet build FlowerShop.sln

# 2. Запуск сервера (в первом терминале)
dotnet run --project Server

# 3. Запуск клиента (во втором терминале)
dotnet run --project Client
```

Порт по умолчанию — 5000. Можно указать свой: `dotnet run --project Server -- 5010`
и `dotnet run --project Client -- 127.0.0.1 5010`.

## Тестирование

```bash
dotnet test FlowerShop.sln
```

Всего 18 тестов — все проходят. Покрыты 4 обязательных сценария (Требование 9)
и дополнительные:

1. **Успешный сценарий** — `BusinessLogicTests.Handle_CreateOrder_WithValidData_ReturnsSuccess`
   и полный жизненный цикл заказа `Handle_FullWorkflow_...` (все 4 операции и события).
2. **Неизвестная операция** — `Handle_UnknownOperation_ReturnsUnknownOperationError`.
3. **Некорректные данные** — пустой адрес, неверная дата, пустой payload,
   несуществующий заказ/флорист (`NOT_FOUND`).
4. **Недоступный сервер** — `NetworkClientTests.Send_ToUnavailableServer_...`.

Также проверяется разбор JSON в `RequestHandlerTests` (битый JSON, пустая операция).

> Примечание: в репозитории лежит локальный `NuGet.config` (только nuget.org),
> чтобы сборка не зависела от источников, прописанных глобально на компьютере.
