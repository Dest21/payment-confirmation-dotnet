namespace Fudis.Payments.Domain;

/// <summary>Regla de negocio de la ganancia del repartidor. Pura: no toca la base de datos.</summary>
public static class DriverEarningPolicy
{
    /// <summary>
    /// Ganancia = tarifa de envío × porcentaje, redondeada a céntimos con mitad hacia arriba
    /// (5.005 → 5.01). Si el pedido aún no tiene repartidor, queda pendiente de asignación.
    /// </summary>
    public static DriverEarning For(Order order, decimal share, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (share is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(share), share, "El porcentaje debe estar entre 0 y 1.");

        return new DriverEarning
        {
            OrderId = order.Id,
            DriverId = order.DriverId,
            Amount = Math.Round(order.DeliveryFee * share, 2, MidpointRounding.AwayFromZero),
            Rate = share,
            Status = order.DriverId is null ? EarningStatus.PendingAssignment : EarningStatus.Payable,
            CreatedAt = now,
        };
    }
}
