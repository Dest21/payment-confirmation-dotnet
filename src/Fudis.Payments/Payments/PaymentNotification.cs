namespace Fudis.Payments.Payments;

/// <summary>
/// Contrato de entrada propuesto (la pasarela real no está definida en el enunciado).
/// Solo las notificaciones con Status = "APPROVED" confirman un pedido.
/// </summary>
public sealed record PaymentNotification(
    string EventId,      // id único de la notificación en la pasarela: clave de idempotencia
    string PaymentId,    // id del cobro; dos cobros distintos para un pedido = doble cargo
    int OrderId,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset OccurredAt)
{
    public const string Approved = "APPROVED";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(EventId) || EventId.Length > 100) errors.Add("eventId es obligatorio (máx. 100).");
        if (string.IsNullOrWhiteSpace(PaymentId) || PaymentId.Length > 100) errors.Add("paymentId es obligatorio (máx. 100).");
        if (OrderId <= 0) errors.Add("orderId debe ser positivo.");
        if (Amount <= 0 || decimal.Round(Amount, 2) != Amount) errors.Add("amount debe ser positivo y con máximo 2 decimales.");
        if (string.IsNullOrWhiteSpace(Currency) || Currency.Length != 3) errors.Add("currency debe ser un código ISO de 3 letras.");
        if (string.IsNullOrWhiteSpace(Status)) errors.Add("status es obligatorio.");
        return errors;
    }
}
