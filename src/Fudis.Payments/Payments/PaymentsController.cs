using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Fudis.Payments.Payments;

[ApiController]
[Route("payments")]
public sealed partial class PaymentsController(
    ConfirmPaymentHandler handler,
    SignatureVerifier verifier,
    ILogger<PaymentsController> log) : ControllerBase
{
    public const string TimestampHeader = "X-Fudis-Timestamp";
    public const string SignatureHeader = "X-Fudis-Signature";
    private const int MaxBodyBytes = 16 * 1024; // una notificación pesa unos cientos de bytes

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpPost("confirm-payment")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> ConfirmPayment(CancellationToken ct)
    {
        // Se lee el cuerpo crudo: la firma se calcula sobre los bytes exactos que envió la pasarela.
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        if (!verifier.IsValid(body, Request.Headers[TimestampHeader], Request.Headers[SignatureHeader]))
        {
            LogInvalidSignature(log);
            return Unauthorized();
        }

        PaymentNotification? notification;
        try
        {
            notification = JsonSerializer.Deserialize<PaymentNotification>(body, Json);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "JSON inválido." });
        }
        if (notification is null)
            return BadRequest(new { error = "Cuerpo vacío." });

        var errors = notification.Validate();
        if (errors.Count > 0)
            return BadRequest(new { errors });

        // Solo un pago aprobado confirma el pedido; los demás estados se aceptan sin efecto.
        if (notification.Status != PaymentNotification.Approved)
            return Ok(new { result = "Ignored" });

        var result = await handler.HandleAsync(notification, ct);
        LogProcessed(log, notification.EventId, notification.OrderId, result);
        return ToResponse(result);
    }

    private IActionResult ToResponse(ConfirmResult result) => result switch
    {
        // 200 en los tres casos: la pasarela no debe seguir reintentando.
        ConfirmResult.Applied or ConfirmResult.Duplicate or ConfirmResult.AlreadyPaid => Ok(new { result = result.ToString() }),
        // 404 sin guardar nada: la pasarela reintentará por si el pedido aún no existía.
        ConfirmResult.OrderNotFound => NotFound(),
        // 4xx con la notificación ya registrada y una alerta en el outbox: reintentar no cambia nada.
        ConfirmResult.AmountMismatch => UnprocessableEntity(new { result = result.ToString() }),
        ConfirmResult.OrderNotPayable => Conflict(new { result = result.ToString() }),
        _ => throw new InvalidOperationException($"Resultado no contemplado: {result}"),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notificación de pago con firma inválida rechazada")]
    private static partial void LogInvalidSignature(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notificación {EventId} del pedido {OrderId}: {Result}")]
    private static partial void LogProcessed(ILogger logger, string eventId, int orderId, ConfirmResult result);
}
