using System.Text.Json;
using Fudis.Payments.Data;
using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Payments.Outbox;

public interface IRestaurantNotifier
{
    /// <summary>
    /// Avisa al restaurante. La entrega es "al menos una vez": el mismo messageId puede
    /// llegar dos veces si el proceso cae justo después de enviar, así que el receptor
    /// debe descartar duplicados por messageId.
    /// </summary>
    Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct);
}

public interface IPaymentReviewAlerter
{
    Task AlertAsync(string payload, Guid messageId, CancellationToken ct);
}

/// <summary>
/// Envía los mensajes pendientes del outbox. Varias instancias pueden correr a la vez:
/// UPDLOCK + READPAST hace que cada una tome filas distintas.
/// </summary>
public sealed class OutboxProcessor(
    FudisDbContext db,
    IRestaurantNotifier restaurants,
    IPaymentReviewAlerter alerts,
    TimeProvider clock,
    ILogger<OutboxProcessor> log)
{
    public const int MaxAttempts = 8;

    public async Task<int> ProcessBatchAsync(int batchSize = 20, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.OutboxMessages
            .FromSql($"""
                SELECT TOP ({batchSize}) * FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ProcessedAt IS NULL AND FailedAt IS NULL AND NextAttemptAt <= {now}
                ORDER BY Id
                """)
            .ToListAsync(ct);

        foreach (var m in batch)
        {
            try
            {
                switch (m.Type)
                {
                    case OutboxMessageType.RestaurantPaymentConfirmed:
                        var orderId = JsonDocument.Parse(m.Payload).RootElement.GetProperty("orderId").GetInt32();
                        await restaurants.NotifyPaymentConfirmedAsync(orderId, m.MessageId, ct);
                        break;
                    case OutboxMessageType.PaymentRequiresReview:
                        await alerts.AlertAsync(m.Payload, m.MessageId, ct);
                        break;
                    default:
                        throw new InvalidOperationException($"Tipo de mensaje desconocido: {m.Type}");
                }
                m.ProcessedAt = now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.Attempts++;
                m.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                if (m.Attempts >= MaxAttempts)
                {
                    m.FailedAt = now;
                    log.LogError(ex, "Mensaje {MessageId} ({Type}) agotó sus reintentos", m.MessageId, m.Type);
                }
                else
                {
                    // Espera exponencial: 2, 4, 8… segundos, hasta 10 minutos.
                    m.NextAttemptAt = now.AddSeconds(Math.Min(600, Math.Pow(2, m.Attempts)));
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }
}

public sealed class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processed = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(20, ct);
                if (processed > 0) continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Error procesando el outbox");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}

/// <summary>Implementaciones de ejemplo: en producción serían push, WhatsApp, cola, etc.</summary>
public sealed class LoggingRestaurantNotifier(ILogger<LoggingRestaurantNotifier> log) : IRestaurantNotifier
{
    public Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct)
    {
        log.LogInformation("Restaurante avisado del pago del pedido {OrderId} ({MessageId})", orderId, messageId);
        return Task.CompletedTask;
    }
}

public sealed class LoggingReviewAlerter(ILogger<LoggingReviewAlerter> log) : IPaymentReviewAlerter
{
    public Task AlertAsync(string payload, Guid messageId, CancellationToken ct)
    {
        log.LogWarning("Pago para revisión manual ({MessageId}): {Payload}", messageId, payload);
        return Task.CompletedTask;
    }
}
