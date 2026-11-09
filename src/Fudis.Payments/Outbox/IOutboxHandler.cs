using Fudis.Payments.Domain;

namespace Fudis.Payments.Outbox;

/// <summary>
/// Envía un tipo de mensaje del outbox. Para agregar un efecto nuevo basta con un record
/// nuevo y su handler; el procesador no cambia.
/// </summary>
public interface IOutboxHandler
{
    /// <summary>Tipo que atiende: el nombre del record del mensaje.</summary>
    string MessageType { get; }

    Task HandleAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>
/// Avisa al restaurante. La entrega es "al menos una vez": el mismo messageId puede llegar
/// dos veces si el proceso cae justo después de enviar, así que el receptor debe descartar
/// duplicados por messageId.
/// </summary>
public interface IRestaurantNotifier
{
    Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct);
}

/// <summary>Lleva a operaciones los pagos que necesitan revisión humana.</summary>
public interface IPaymentReviewAlerter
{
    Task AlertAsync(PaymentReviewRequested review, Guid messageId, CancellationToken ct);
}

public sealed class RestaurantPaymentConfirmedHandler(IRestaurantNotifier notifier) : IOutboxHandler
{
    public string MessageType => nameof(RestaurantPaymentConfirmed);

    public Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return notifier.NotifyPaymentConfirmedAsync(message.Read<RestaurantPaymentConfirmed>().OrderId, message.MessageId, ct);
    }
}

public sealed class PaymentReviewRequestedHandler(IPaymentReviewAlerter alerter) : IOutboxHandler
{
    public string MessageType => nameof(PaymentReviewRequested);

    public Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return alerter.AlertAsync(message.Read<PaymentReviewRequested>(), message.MessageId, ct);
    }
}
