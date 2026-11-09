using System.Globalization;

namespace Fudis.Payments.Domain;

/// <summary>Lo que se sabe del pedido cuando una notificación no pudo pagarlo.</summary>
public sealed record OrderSnapshot(string Status, decimal Total, string Currency, string? PaidPaymentId);

/// <summary>
/// Por qué no se aplicó un pago y si hace falta revisión humana.
/// <see cref="ReviewReason"/> nulo significa que no hay nada que revisar.
/// </summary>
public sealed record PaymentDecision(string Outcome, string? ReviewReason);

/// <summary>Reglas de negocio de un pago que no pudo aplicarse. Puras: no tocan la base de datos.</summary>
public static class PaymentRules
{
    public static PaymentDecision ExplainNotApplied(OrderSnapshot order, string paymentId, decimal amount, string currency)
    {
        ArgumentNullException.ThrowIfNull(order);

        return order.Status switch
        {
            // Mismo cobro que ya pagó el pedido: es un reintento de la pasarela, no hay nada que revisar.
            OrderStatus.Paid when order.PaidPaymentId == paymentId =>
                new PaymentDecision(NotificationOutcome.AlreadyPaid, null),

            // Otro cobro para un pedido ya pagado: el cliente pudo pagar dos veces.
            OrderStatus.Paid => new PaymentDecision(NotificationOutcome.AlreadyPaid,
                $"Cobro {paymentId} para un pedido ya pagado con {order.PaidPaymentId}: posible doble cargo."),

            // Sigue pendiente: el UPDATE no aplicó porque el importe o la moneda no coinciden.
            OrderStatus.PendingPayment => new PaymentDecision(NotificationOutcome.AmountMismatch,
                $"Importe recibido {Money(amount, currency)}, esperado {Money(order.Total, order.Currency)}."),

            // Cancelado u otro estado que no admite pago: hay que reembolsar.
            _ => new PaymentDecision(NotificationOutcome.OrderNotPayable,
                $"Pago recibido para un pedido en estado {order.Status}: revisar reembolso."),
        };
    }

    private static string Money(decimal amount, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:0.00} {currency.Trim()}");
}
