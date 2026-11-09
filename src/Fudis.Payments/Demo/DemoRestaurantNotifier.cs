using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Fudis.Payments.Outbox;

namespace Fudis.Payments.Demo;

/// <summary>
/// Restaurante simulado para la demo: guarda los avisos que recibe y se puede "caer"
/// a voluntad para mostrar los reintentos del outbox.
/// </summary>
public sealed class DemoRestaurantNotifier : IRestaurantNotifier
{
    private readonly ConcurrentQueue<ReceivedNotice> _received = new();

    private volatile bool _down;

    /// <summary>Si está caído, cada aviso falla y el outbox lo reintenta.</summary>
    public bool Down
    {
        get => _down;
        set => _down = value;
    }

    public sealed record ReceivedNotice(int OrderId, Guid? MessageId, string Source, DateTime At);

    public Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct) =>
        Receive(orderId, messageId, "solución");

    public Task Receive(int orderId, Guid? messageId, string source)
    {
        if (Down) throw new HttpRequestException("El servicio del restaurante no responde (simulado).");
        _received.Enqueue(new ReceivedNotice(orderId, messageId, source, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public IReadOnlyList<ReceivedNotice> Received => _received.Reverse().Take(300).ToList();

    public void Clear() => _received.Clear();
}

public static class DemoServiceCollectionExtensions
{
    public const string RateLimitPolicy = "demo";

    /// <summary>
    /// Solo con Demo:Enabled: restaurante que se puede "caer", cliente HTTP para el simulador y un
    /// límite por IP para las acciones que escriben (la demo puede estar publicada).
    /// </summary>
    public static IServiceCollection AddPaymentsDemo(this IServiceCollection services)
    {
        services.AddSingleton<DemoRestaurantNotifier>();
        services.AddSingleton<IRestaurantNotifier>(sp => sp.GetRequiredService<DemoRestaurantNotifier>());
        services.AddHttpClient();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                // Detrás de Cloudflare la IP real viene en CF-Connecting-IP.
                http.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                    ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }
}
