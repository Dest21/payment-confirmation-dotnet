namespace Fudis.Payments.Domain;

public static class OrderStatus
{
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string Paid = "PAID";
    public const string Cancelled = "CANCELLED";
}

public class Order
{
    public int Id { get; set; }
    public string Status { get; set; } = OrderStatus.PendingPayment;
    public int? DriverId { get; set; }
    public decimal DeliveryFee { get; set; }

    // Supuesto: el pedido guarda el total cobrado al cliente y su moneda.
    // Sin esto no se puede validar el importe que informa la pasarela.
    public decimal Total { get; set; }
    public string Currency { get; set; } = "PEN";
    public DateTime? PaidAt { get; set; }

    /// <summary>Cobro de la pasarela que pagó el pedido; detecta dobles cargos sin leer otras notificaciones.</summary>
    public string? PaidPaymentId { get; set; }
}

public static class EarningStatus
{
    /// <summary>El pedido se pagó sin repartidor: la ganancia se completa cuando se asigne uno.</summary>
    public const string PendingAssignment = "PENDING_ASSIGNMENT";
    public const string Payable = "PAYABLE";
}

public class DriverEarning
{
    public long Id { get; set; }
    public int OrderId { get; set; }
    public int? DriverId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Porcentaje aplicado, guardado para auditar si la regla cambia.</summary>
    public decimal Rate { get; set; }
    public string Status { get; set; } = EarningStatus.Payable;
    public DateTime CreatedAt { get; set; }
}

public static class NotificationOutcome
{
    public const string Received = "RECEIVED";
    public const string Applied = "APPLIED";
    public const string AlreadyPaid = "ALREADY_PAID";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string OrderNotPayable = "ORDER_NOT_PAYABLE";
}

/// <summary>
/// Cada notificación de la pasarela que se procesó. El índice único sobre
/// ProviderEventId es lo que hace idempotente el endpoint.
/// </summary>
public class PaymentNotificationRecord
{
    public long Id { get; set; }
    public string ProviderEventId { get; set; } = "";
    public string PaymentId { get; set; } = "";
    public int OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Outcome { get; set; } = NotificationOutcome.Received;
    public DateTime ReceivedAt { get; set; }
}

public static class OutboxMessageType
{
    public const string RestaurantPaymentConfirmed = "RestaurantPaymentConfirmed";
    public const string PaymentRequiresReview = "PaymentRequiresReview";
}

/// <summary>
/// Efecto externo pendiente (avisar al restaurante, alertar a operaciones).
/// Se guarda en la misma transacción que el pago y se envía después.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage Create(string type, object payload, DateTime now) => new()
    {
        MessageId = Guid.NewGuid(),
        Type = type,
        Payload = System.Text.Json.JsonSerializer.Serialize(payload),
        CreatedAt = now,
        NextAttemptAt = now,
    };
}
