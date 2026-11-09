namespace Fudis.Payments.Domain;

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
