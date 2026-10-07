-- ============================================================================
-- ЛР №4. FlowerShop (вариант 14 — «Заказ и доставка цветов»).
-- Схема базы данных: 7 таблиц с первичными/внешними ключами и ограничениями.
-- Скрипт идемпотентен: повторный запуск не приводит к ошибкам.
-- ============================================================================

IF DB_ID(N'FlowerShop') IS NULL
BEGIN
    CREATE DATABASE FlowerShop;
END
GO

USE FlowerShop;
GO

-- 1. Клиенты ---------------------------------------------------------------
IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
CREATE TABLE dbo.Customers
(
    Id       INT           IDENTITY(1, 1) NOT NULL,
    FullName NVARCHAR(100) NOT NULL,
    Phone    NVARCHAR(30)  NOT NULL,
    Address  NVARCHAR(200) NOT NULL,
    CONSTRAINT PK_Customers PRIMARY KEY (Id)
);
GO

-- 2. Букеты (общий ресурс хранится в AvailableCount) -----------------------
IF OBJECT_ID(N'dbo.Bouquets', N'U') IS NULL
CREATE TABLE dbo.Bouquets
(
    Id             INT           IDENTITY(1, 1) NOT NULL,
    Name           NVARCHAR(100) NOT NULL,
    AvailableCount INT           NOT NULL
        CONSTRAINT CK_Bouquets_AvailableCount CHECK (AvailableCount >= 0),
    CONSTRAINT PK_Bouquets PRIMARY KEY (Id)
);
GO

-- 3. Состав букета (цветы) --------------------------------------------------
IF OBJECT_ID(N'dbo.FlowerItems', N'U') IS NULL
CREATE TABLE dbo.FlowerItems
(
    Id           INT             IDENTITY(1, 1) NOT NULL,
    BouquetId    INT             NOT NULL,
    FlowerName   NVARCHAR(100)   NOT NULL,
    Count        INT             NOT NULL
        CONSTRAINT CK_FlowerItems_Count CHECK (Count > 0),
    PricePerUnit DECIMAL(10, 2)  NOT NULL
        CONSTRAINT CK_FlowerItems_Price CHECK (PricePerUnit >= 0),
    CONSTRAINT PK_FlowerItems PRIMARY KEY (Id),
    CONSTRAINT FK_FlowerItems_Bouquets FOREIGN KEY (BouquetId)
        REFERENCES dbo.Bouquets (Id) ON DELETE CASCADE
);
GO

-- 4. Флористы --------------------------------------------------------------
IF OBJECT_ID(N'dbo.Florists', N'U') IS NULL
CREATE TABLE dbo.Florists
(
    Id        INT           IDENTITY(1, 1) NOT NULL,
    FullName  NVARCHAR(100) NOT NULL,
    Specialty NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_Florists PRIMARY KEY (Id)
-- 5. Заказы ----------------------------------------------------------------
IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
CREATE TABLE dbo.Orders
(
    Id              INT           IDENTITY(1, 1) NOT NULL,
    CustomerId      INT           NOT NULL,
    BouquetId       INT           NOT NULL,
    DeliveryDate    DATE          NOT NULL,
    Status          NVARCHAR(30)  NOT NULL
        CONSTRAINT CK_Orders_Status CHECK
            (Status IN (N'Created', N'BouquetPrepared', N'FloristAssigned',
                        N'DeliveryArranged', N'Cancelled')),
    FloristId       INT           NULL,
    DeliveryAddress NVARCHAR(200) NOT NULL,
    CONSTRAINT PK_Orders PRIMARY KEY (Id),
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId)
        REFERENCES dbo.Customers (Id),
    CONSTRAINT FK_Orders_Bouquets FOREIGN KEY (BouquetId)
        REFERENCES dbo.Bouquets (Id),
    CONSTRAINT FK_Orders_Florists FOREIGN KEY (FloristId)
        REFERENCES dbo.Florists (Id)
);
GO

-- 6. Доставки --------------------------------------------------------------
IF OBJECT_ID(N'dbo.Deliveries', N'U') IS NULL
CREATE TABLE dbo.Deliveries
(
    Id        INT           IDENTITY(1, 1) NOT NULL,
    OrderId   INT           NOT NULL,
    Address   NVARCHAR(200) NOT NULL,
    PlannedAt DATETIME2     NULL,
    Status    NVARCHAR(30)  NOT NULL
        CONSTRAINT CK_Deliveries_Status CHECK
            (Status IN (N'Planned', N'OnTheWay', N'Delivered', N'Cancelled')),
    CONSTRAINT PK_Deliveries PRIMARY KEY (Id),
    CONSTRAINT UQ_Deliveries_Order UNIQUE (OrderId),
    CONSTRAINT FK_Deliveries_Orders FOREIGN KEY (OrderId)
        REFERENCES dbo.Orders (Id) ON DELETE CASCADE
);
GO

-- 7. События заказов (журнал жизненного цикла) ------------------------------
IF OBJECT_ID(N'dbo.OrderEvents', N'U') IS NULL
CREATE TABLE dbo.OrderEvents
(
    Id        INT          IDENTITY(1, 1) NOT NULL,
    OrderId   INT          NOT NULL,
    Event     NVARCHAR(50) NOT NULL,
    CreatedAt DATETIME2    NOT NULL
        CONSTRAINT DF_OrderEvents_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_OrderEvents PRIMARY KEY (Id),
    CONSTRAINT FK_OrderEvents_Orders FOREIGN KEY (OrderId)
        REFERENCES dbo.Orders (Id) ON DELETE CASCADE
);
GO
);
GO


-- ---------------------------------------------------------------------------
-- ---------------------------------------------------------------------------
-- Демонстрационные данные (вставляются только при пустых таблицах)
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.Customers)
BEGIN
    INSERT INTO dbo.Customers (FullName, Phone, Address) VALUES
        (N'Иванова Анна',  N'+7-912-345-67-89', N'ул. Ленина, 10'),
        (N'Петров Сергей', N'+7-923-456-78-90', N'пр. Мира, 25');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Bouquets)
BEGIN
    DECLARE @b INT;

    -- Букет 1: общий ресурс = 3
    INSERT INTO dbo.Bouquets (Name, AvailableCount) VALUES (N'Розовое облако', 3);
    SET @b = SCOPE_IDENTITY();
    INSERT INTO dbo.FlowerItems (BouquetId, FlowerName, Count, PricePerUnit)
        VALUES (@b, N'Роза', 7, 120.00), (@b, N'Эвкалипт', 3, 40.00);

    -- Букет 2: общий ресурс = 5
    INSERT INTO dbo.Bouquets (Name, AvailableCount) VALUES (N'Весенний', 5);
    SET @b = SCOPE_IDENTITY();
    INSERT INTO dbo.FlowerItems (BouquetId, FlowerName, Count, PricePerUnit)
        VALUES (@b, N'Тюльпан', 15, 60.00);

    -- Букет 3: общий ресурс = 1 — для эксперимента «Resource = 1»
    INSERT INTO dbo.Bouquets (Name, AvailableCount) VALUES (N'Новогодний люкс', 1);
    SET @b = SCOPE_IDENTITY();
    INSERT INTO dbo.FlowerItems (BouquetId, FlowerName, Count, PricePerUnit)
        VALUES (@b, N'Роза', 15, 200.00);
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Florists)
BEGIN
    INSERT INTO dbo.Florists (FullName, Specialty) VALUES
        (N'Смирнова Ольга',  N'свадебные букеты'),
        (N'Кузнецова Мария', N'букеты на день рождения');
END
GO
