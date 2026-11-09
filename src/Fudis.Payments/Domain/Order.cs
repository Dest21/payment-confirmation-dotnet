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

    /// <summary>Cobro de la pasarela que pagó el pedido; permite detectar dobles cargos.</summary>
    public string? PaidPaymentId { get; set; }
}
