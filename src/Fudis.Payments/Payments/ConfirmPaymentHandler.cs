using Fudis.Payments.Data;
using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Fudis.Payments.Payments;

public enum ConfirmResult
{
    Applied,          // el pedido pasó a PAID en esta llamada
    Duplicate,        // esta misma notificación ya se había procesado
    AlreadyPaid,      // otra notificación ya había pagado el pedido
    AmountMismatch,   // el importe o la moneda no coinciden con el pedido
    OrderNotPayable,  // el pedido no admite pago (p. ej. cancelado)
    OrderNotFound,
}

/// <summary>
/// Caso de uso "confirmar un pago". Todo ocurre en una transacción:
/// <list type="number">
///   <item>registra la notificación (índice único por EventId → idempotencia);</item>
///   <item>pasa el pedido de PENDING_PAYMENT a PAID con un UPDATE condicional (un solo ganador
///         aunque lleguen notificaciones en paralelo);</item>
///   <item>registra la ganancia del repartidor (índice único por OrderId);</item>
///   <item>deja en el outbox el aviso al restaurante.</item>
/// </list>
/// O se guarda todo o nada. Las garantías dependen de SQL Server a propósito: por eso este
/// caso de uso trabaja con el DbContext y no con un repositorio genérico.
/// </summary>
public sealed class ConfirmPaymentHandler(FudisDbContext db, IOptions<PaymentOptions> options, TimeProvider clock)
{
    public async Task<ConfirmResult> HandleAsync(PaymentNotification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var record = await TryRegisterAsync(notification, now, tx, ct);
        if (record is null)
            return ConfirmResult.Duplicate;

        if (await TryMarkPaidAsync(notification, now, ct))
        {
            await RecordPaymentEffectsAsync(notification.OrderId, now, ct);
            record.Outcome = NotificationOutcome.Applied;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ConfirmResult.Applied;
        }

        var order = await db.Orders.AsNoTracking()
            .Where(o => o.Id == notification.OrderId)
            .Select(o => new OrderSnapshot(o.Status, o.Total, o.Currency, o.PaidPaymentId))
            .SingleOrDefaultAsync(ct);

        if (order is null)
        {
            // No se guarda la notificación: la pasarela reintentará y el pedido podría existir luego.
            await tx.RollbackAsync(ct);
            return ConfirmResult.OrderNotFound;
        }

        var decision = PaymentRules.ExplainNotApplied(order, notification.PaymentId, notification.Amount, notification.Currency);
        record.Outcome = decision.Outcome;
        if (decision.ReviewReason is not null)
        {
            db.OutboxMessages.Add(OutboxMessage.Create(new PaymentReviewRequested(
                notification.OrderId, notification.EventId, notification.PaymentId,
                notification.Amount, notification.Currency, decision.ReviewReason), now));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToResult(decision.Outcome);
    }

    /// <summary>
    /// Inserta la notificación. Si otra petición con el mismo EventId está en curso, SQL Server
    /// bloquea este INSERT hasta que la otra termine; luego falla por clave duplicada.
    /// Devuelve null si la notificación ya se había procesado.
    /// </summary>
    private async Task<PaymentNotificationRecord?> TryRegisterAsync(
        PaymentNotification n, DateTime now, IDbContextTransaction tx, CancellationToken ct)
    {
        var record = new PaymentNotificationRecord
        {
            ProviderEventId = n.EventId,
            PaymentId = n.PaymentId,
            OrderId = n.OrderId,
            Amount = n.Amount,
            Currency = n.Currency,
            ReceivedAt = now,
        };
        db.PaymentNotifications.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
            return record;
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            return null;
        }
    }

    /// <summary>
    /// Transición atómica: solo cambia si el pedido sigue pendiente y el importe coincide.
    /// Si dos peticiones compiten, la segunda espera el bloqueo de la fila y, al volver a
    /// evaluar la condición, ya no encuentra el pedido pendiente.
    /// </summary>
    private async Task<bool> TryMarkPaidAsync(PaymentNotification n, DateTime now, CancellationToken ct)
    {
        var updated = await db.Orders
            .Where(o => o.Id == n.OrderId
                        && o.Status == OrderStatus.PendingPayment
                        && o.Total == n.Amount
                        && o.Currency == n.Currency)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatus.Paid)
                .SetProperty(o => o.PaidAt, now)
                .SetProperty(o => o.PaidPaymentId, n.PaymentId), ct);
        return updated == 1;
    }

    /// <summary>Ganancia del repartidor y aviso al restaurante, en la misma transacción que el pago.</summary>
    private async Task RecordPaymentEffectsAsync(int orderId, DateTime now, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        db.DriverEarnings.Add(DriverEarningPolicy.For(order, options.Value.DriverShare, now));
        db.OutboxMessages.Add(OutboxMessage.Create(new RestaurantPaymentConfirmed(order.Id), now));
    }

    private static ConfirmResult ToResult(string outcome) => outcome switch
    {
        NotificationOutcome.AlreadyPaid => ConfirmResult.AlreadyPaid,
        NotificationOutcome.AmountMismatch => ConfirmResult.AmountMismatch,
        NotificationOutcome.OrderNotPayable => ConfirmResult.OrderNotPayable,
        _ => throw new InvalidOperationException($"Resultado inesperado: {outcome}"),
    };
}
