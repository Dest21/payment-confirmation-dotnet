using Fudis.Payments.Domain;

namespace Fudis.Payments.Outbox;

/// <summary>Implementación de ejemplo: en producción sería push, WhatsApp, una cola, etc.</summary>
public sealed partial class LoggingRestaurantNotifier(ILogger<LoggingRestaurantNotifier> log) : IRestaurantNotifier
{
    public Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct)
    {
        LogNotified(log, orderId, messageId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restaurante avisado del pago del pedido {OrderId} ({MessageId})")]
    private static partial void LogNotified(ILogger logger, int orderId, Guid messageId);
}

/// <summary>Implementación de ejemplo: en producción sería Slack, correo o un panel de operaciones.</summary>
public sealed partial class LoggingReviewAlerter(ILogger<LoggingReviewAlerter> log) : IPaymentReviewAlerter
{
    public Task AlertAsync(PaymentReviewRequested review, Guid messageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);
        LogReview(log, review.OrderId, review.Reason, messageId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pago del pedido {OrderId} para revisión manual: {Reason} ({MessageId})")]
    private static partial void LogReview(ILogger logger, int orderId, string reason, Guid messageId);
}
