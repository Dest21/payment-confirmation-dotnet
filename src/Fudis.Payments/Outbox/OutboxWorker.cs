namespace Fudis.Payments.Outbox;

/// <summary>Ejecuta el procesador del outbox en segundo plano mientras corre la aplicación.</summary>
public sealed partial class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                if (await processor.ProcessBatchAsync(ct: stoppingToken) > 0)
                    continue; // hay más trabajo: seguir sin esperar
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(log, ex);
            }
            await Task.Delay(IdleDelay, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error procesando el outbox")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
