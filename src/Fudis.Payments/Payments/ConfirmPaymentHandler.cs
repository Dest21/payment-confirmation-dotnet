using Fudis.Payments.Data;
using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;
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
/// Confirma un pago en una sola transacción:
///   1. registra la notificación (índice único por EventId → idempotencia),
///   2. pasa el pedido de PENDING_PAYMENT a PAID con un UPDATE condicional (un solo ganador
///      aunque lleguen notificaciones en paralelo),
///   3. registra la ganancia del repartidor (índice único por OrderId),
///   4. deja en el outbox el aviso al restaurante.
/// O se guarda todo o nada. El aviso se envía después, con reintentos (OutboxProcessor).
/// </summary>
public sealed class ConfirmPaymentHandler(FudisDbContext db, IOptions<PaymentOptions> options, TimeProvider clock)
{
    public async Task<ConfirmResult> HandleAsync(PaymentNotification n, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Idempotencia. Si otra petición con el mismo EventId está en curso, SQL Server
        //    bloquea este INSERT hasta que la otra termine; luego falla por clave duplicada.
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
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            return ConfirmResult.Duplicate;
        }

        // 2. Transición atómica. Solo cambia si sigue pendiente y el importe coincide;
        //    si dos peticiones compiten, la segunda espera el bloqueo de fila y, al
        //    re-evaluar la condición, ya no encuentra el pedido pendiente.
        var paid = await db.Orders
            .Where(o => o.Id == n.OrderId
                        && o.Status == OrderStatus.PendingPayment
                        && o.Total == n.Amount
                        && o.Currency == n.Currency)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatus.Paid)
                .SetProperty(o => o.PaidAt, now)
                .SetProperty(o => o.PaidPaymentId, n.PaymentId), ct);

        if (paid == 1)
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == n.OrderId, ct);
            var rate = options.Value.DriverShare;

            // 3. Ganancia del repartidor. Redondeo a céntimos, mitad hacia arriba (supuesto).
            db.DriverEarnings.Add(new DriverEarning
            {
                OrderId = order.Id,
                DriverId = order.DriverId,
                Amount = Math.Round(order.DeliveryFee * rate, 2, MidpointRounding.AwayFromZero),
                Rate = rate,
                Status = order.DriverId is null ? EarningStatus.PendingAssignment : EarningStatus.Payable,
                CreatedAt = now,
            });

            // 4. Aviso al restaurante: queda guardado con el pago, se envía después.
            db.OutboxMessages.Add(OutboxMessage.Create(
                OutboxMessageType.RestaurantPaymentConfirmed, new { orderId = order.Id }, now));

            record.Outcome = NotificationOutcome.Applied;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ConfirmResult.Applied;
        }

        // No hubo transición: averiguar por qué.
        var current = await db.Orders.AsNoTracking()
            .Where(o => o.Id == n.OrderId)
            .Select(o => new { o.Status, o.Total, o.Currency, o.PaidPaymentId })
            .SingleOrDefaultAsync(ct);

        if (current is null)
        {
            // No se guarda la notificación: la pasarela reintentará y el pedido podría existir luego.
            await tx.RollbackAsync(ct);
            return ConfirmResult.OrderNotFound;
        }

        ConfirmResult result;
        if (current.Status == OrderStatus.Paid)
        {
            record.Outcome = NotificationOutcome.AlreadyPaid;
            result = ConfirmResult.AlreadyPaid;

            // Mismo pedido, pero un cobro distinto al que lo pagó: posible doble cargo al cliente.
            // Se compara con el pedido (ya leído) y no con otras notificaciones: leer filas de
            // transacciones concurrentes sin confirmar podría bloquearlas entre sí.
            if (current.PaidPaymentId != n.PaymentId)
                AddReview(n, $"Cobro {n.PaymentId} para un pedido ya pagado con {current.PaidPaymentId}: posible doble cargo.", now);
        }
        else if (current.Status == OrderStatus.PendingPayment)
        {
            record.Outcome = NotificationOutcome.AmountMismatch;
            result = ConfirmResult.AmountMismatch;
            AddReview(n, $"Importe recibido {n.Amount} {n.Currency}, esperado {current.Total} {current.Currency}.", now);
        }
        else
        {
            record.Outcome = NotificationOutcome.OrderNotPayable;
            result = ConfirmResult.OrderNotPayable;
            AddReview(n, $"Pago recibido para un pedido en estado {current.Status}: revisar reembolso.", now);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private void AddReview(PaymentNotification n, string reason, DateTime now) =>
        db.OutboxMessages.Add(OutboxMessage.Create(OutboxMessageType.PaymentRequiresReview,
            new { n.OrderId, n.EventId, n.PaymentId, n.Amount, n.Currency, reason }, now));
}
