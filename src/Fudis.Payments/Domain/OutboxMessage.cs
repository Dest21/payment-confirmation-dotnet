using System.Text.Json;

namespace Fudis.Payments.Domain;

/// <summary>
/// Efecto externo pendiente (avisar al restaurante, alertar a operaciones). Se guarda en
/// la misma transacción que el pago y lo envía después el procesador del outbox.
/// </summary>
public class OutboxMessage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>El tipo del mensaje es el nombre del record; el contenido se guarda en JSON.</summary>
    public static OutboxMessage Create<T>(T payload, DateTime now) where T : notnull => new()
    {
        MessageId = Guid.NewGuid(),
        Type = typeof(T).Name,
        Payload = JsonSerializer.Serialize(payload, Json),
        CreatedAt = now,
        NextAttemptAt = now,
    };

    public T Read<T>() =>
        JsonSerializer.Deserialize<T>(Payload, Json)
        ?? throw new InvalidOperationException($"Mensaje {MessageId} sin contenido.");
}

/// <summary>El pago de un pedido quedó confirmado: hay que avisar al restaurante.</summary>
public sealed record RestaurantPaymentConfirmed(int OrderId);

/// <summary>Un pago necesita revisión humana (importe distinto, pedido no pagable, doble cargo).</summary>
public sealed record PaymentReviewRequested(
    int OrderId, string EventId, string PaymentId, decimal Amount, string Currency, string Reason);
