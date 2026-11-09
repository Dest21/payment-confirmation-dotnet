/*
  Migración sobre el esquema existente (SQL Server).
  Supuesto: ya existen Orders(Id, Status, DriverId, DeliveryFee) y DriverEarnings(OrderId, DriverId, Amount).
  Se ejecuta en una ventana de mantenimiento; el paso 0 debe devolver 0 filas antes de seguir.
*/

-- 0. Antes del índice único: ¿ya hay pedidos con más de una ganancia (dobles pagos históricos)?
SELECT OrderId, COUNT(*) AS Ganancias, SUM(Amount) AS Total
FROM dbo.DriverEarnings
GROUP BY OrderId
HAVING COUNT(*) > 1;
-- Si hay filas: conciliarlas con negocio (y con lo pagado a repartidores) ANTES de continuar.

BEGIN TRANSACTION;

-- 1. Pedido: total y moneda para validar el importe, cobro que lo pagó y fecha de pago.
ALTER TABLE dbo.Orders ADD
    Total          decimal(10,2) NULL,
    Currency       char(3)       NULL,
    PaidAt         datetime2     NULL,
    PaidPaymentId  varchar(100)  NULL;
-- Total/Currency se completan desde el sistema de pedidos y luego pasan a NOT NULL.

ALTER TABLE dbo.Orders WITH CHECK ADD CONSTRAINT CK_Orders_Status
    CHECK (Status IN ('PENDING_PAYMENT', 'PAID', 'CANCELLED'));   -- ajustar a los estados reales

-- 2. Ganancias: porcentaje aplicado, estado, fecha y UNA por pedido.
ALTER TABLE dbo.DriverEarnings ALTER COLUMN DriverId int NULL;    -- pedido pagado sin repartidor
ALTER TABLE dbo.DriverEarnings ADD
    Rate      decimal(5,4) NOT NULL CONSTRAINT DF_DriverEarnings_Rate DEFAULT (0.70),
    Status    varchar(30)  NOT NULL CONSTRAINT DF_DriverEarnings_Status DEFAULT ('PAYABLE'),
    CreatedAt datetime2    NOT NULL CONSTRAINT DF_DriverEarnings_CreatedAt DEFAULT (SYSUTCDATETIME());
CREATE UNIQUE INDEX IX_DriverEarnings_OrderId ON dbo.DriverEarnings (OrderId);

-- 3. Registro de notificaciones de la pasarela: el índice único es la idempotencia.
CREATE TABLE dbo.PaymentNotifications (
    Id              bigint IDENTITY PRIMARY KEY,
    ProviderEventId varchar(100)  NOT NULL,
    PaymentId       varchar(100)  NOT NULL,
    OrderId         int           NOT NULL,
    Amount          decimal(10,2) NOT NULL,
    Currency        char(3)       NOT NULL,
    Outcome         varchar(30)   NOT NULL,
    ReceivedAt      datetime2     NOT NULL
);
CREATE UNIQUE INDEX IX_PaymentNotifications_ProviderEventId ON dbo.PaymentNotifications (ProviderEventId);
CREATE INDEX IX_PaymentNotifications_OrderId ON dbo.PaymentNotifications (OrderId);

-- 4. Outbox: efectos externos guardados en la misma transacción que el pago.
CREATE TABLE dbo.OutboxMessages (
    Id            bigint IDENTITY PRIMARY KEY,
    MessageId     uniqueidentifier NOT NULL,
    Type          varchar(100)     NOT NULL,
    Payload       nvarchar(max)    NOT NULL,
    CreatedAt     datetime2        NOT NULL,
    NextAttemptAt datetime2        NOT NULL,
    Attempts      int              NOT NULL DEFAULT (0),
    ProcessedAt   datetime2        NULL,
    FailedAt      datetime2        NULL,
    LastError     nvarchar(1000)   NULL
);
CREATE UNIQUE INDEX IX_OutboxMessages_MessageId ON dbo.OutboxMessages (MessageId);
CREATE INDEX IX_OutboxMessages_Pending ON dbo.OutboxMessages (NextAttemptAt)
    WHERE ProcessedAt IS NULL AND FailedAt IS NULL;

COMMIT;
