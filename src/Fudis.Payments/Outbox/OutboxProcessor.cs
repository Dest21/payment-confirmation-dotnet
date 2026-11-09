using Fudis.Payments.Data;
using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Payments.Outbox;

/// <summary>
/// Envía los mensajes pendientes del outbox. Varias instancias pueden correr a la vez:
/// UPDLOCK + READPAST hace que cada una tome filas distintas.
/// </summary>
public sealed partial class OutboxProcessor
{
    public const int MaxAttempts = 8;
    private const int MaxBackoffSeconds = 600;

    private readonly FudisDbContext _db;
    private readonly Dictionary<string, IOutboxHandler> _handlers;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxProcessor> _log;

    public OutboxProcessor(FudisDbContext db, IEnumerable<IOutboxHandler> handlers, TimeProvider clock, ILogger<OutboxProcessor> log)
    {
        _db = db;
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToDictionary(h => h.MessageType);
        _clock = clock;
        _log = log;
    }

    /// <summary>Procesa un lote. Devuelve cuántos mensajes tomó (enviados o reprogramados).</summary>
    public async Task<int> ProcessBatchAsync(int batchSize = 20, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var batch = await _db.OutboxMessages
            .FromSql($"""
                SELECT TOP ({batchSize}) * FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ProcessedAt IS NULL AND FailedAt IS NULL AND NextAttemptAt <= {now}
                ORDER BY Id
                """)
            .ToListAsync(ct);

        foreach (var message in batch)
            await DispatchAsync(message, now, ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }

    private async Task DispatchAsync(OutboxMessage message, DateTime now, CancellationToken ct)
    {
        try
        {
            if (!_handlers.TryGetValue(message.Type, out var handler))
                throw new InvalidOperationException($"No hay handler para el tipo {message.Type}.");

            await handler.HandleAsync(message, ct);
            message.ProcessedAt = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message.Attempts++;
            message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            if (message.Attempts >= MaxAttempts)
            {
                message.FailedAt = now;
                LogGaveUp(_log, ex, message.MessageId, message.Type, message.Attempts);
            }
            else
            {
                // Espera exponencial: 2, 4, 8… segundos, hasta 10 minutos.
                message.NextAttemptAt = now.AddSeconds(Math.Min(MaxBackoffSeconds, Math.Pow(2, message.Attempts)));
                LogWillRetry(_log, message.MessageId, message.Type, message.Attempts, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mensaje {MessageId} ({Type}) falló en el intento {Attempt}: {Error}")]
    private static partial void LogWillRetry(ILogger logger, Guid messageId, string type, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje {MessageId} ({Type}) agotó sus {Attempts} intentos")]
    private static partial void LogGaveUp(ILogger logger, Exception ex, Guid messageId, string type, int attempts);
}
