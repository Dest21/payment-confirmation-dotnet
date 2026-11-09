namespace Fudis.Payments.Domain;

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
/// <see cref="ProviderEventId"/> es lo que hace idempotente el endpoint.
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
