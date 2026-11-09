using System.Collections.Concurrent;
using Fudis.Payments.Outbox;

namespace Fudis.Payments.Demo;

/// <summary>
/// Restaurante simulado para la demo: guarda los avisos que recibe y se puede "caer"
/// a voluntad para mostrar los reintentos del outbox.
/// </summary>
public sealed class DemoRestaurantNotifier : IRestaurantNotifier
{
    private readonly ConcurrentQueue<ReceivedNotice> _received = new();

    public volatile bool Down;

    public sealed record ReceivedNotice(int OrderId, Guid? MessageId, string Source, DateTime At);

    public Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct) =>
        Receive(orderId, messageId, "solución");

    public Task Receive(int orderId, Guid? messageId, string source)
    {
        if (Down) throw new HttpRequestException("El servicio del restaurante no responde (simulado).");
        _received.Enqueue(new ReceivedNotice(orderId, messageId, source, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public IReadOnlyList<ReceivedNotice> Received => _received.Reverse().Take(30).ToList();

    public void Clear() => _received.Clear();
}
