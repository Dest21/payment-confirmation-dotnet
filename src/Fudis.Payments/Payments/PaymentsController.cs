using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Fudis.Payments.Payments;

[ApiController]
[Route("payments")]
public sealed class PaymentsController(
    ConfirmPaymentHandler handler,
    SignatureVerifier verifier,
    ILogger<PaymentsController> log) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpPost("confirm-payment")]
    public async Task<IActionResult> ConfirmPayment(CancellationToken ct)
    {
        // Se lee el cuerpo crudo: la firma se calcula sobre los bytes exactos que envió la pasarela.
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        if (!verifier.IsValid(body, Request.Headers["X-Fudis-Timestamp"], Request.Headers["X-Fudis-Signature"]))
        {
            log.LogWarning("Notificación de pago con firma inválida rechazada");
            return Unauthorized();
        }

        PaymentNotification? n;
        try { n = JsonSerializer.Deserialize<PaymentNotification>(body, Json); }
        catch (JsonException) { return BadRequest(new { error = "JSON inválido." }); }
        if (n is null) return BadRequest(new { error = "Cuerpo vacío." });

        var errors = n.Validate();
        if (errors.Count > 0) return BadRequest(new { errors });

        // Solo un pago aprobado confirma el pedido; los demás estados se aceptan sin efecto.
        if (n.Status != PaymentNotification.Approved)
            return Ok(new { result = "IGNORED" });

        var result = await handler.HandleAsync(n, ct);
        log.LogInformation("Notificación {EventId} del pedido {OrderId}: {Result}", n.EventId, n.OrderId, result);

        return result switch
        {
            // 200 en los tres casos: la pasarela no debe seguir reintentando.
            ConfirmResult.Applied or ConfirmResult.Duplicate or ConfirmResult.AlreadyPaid => Ok(new { result = result.ToString() }),
            ConfirmResult.OrderNotFound => NotFound(),
            ConfirmResult.AmountMismatch => UnprocessableEntity(new { result = result.ToString() }),
            ConfirmResult.OrderNotPayable => Conflict(new { result = result.ToString() }),
            _ => StatusCode(500),
        };
    }
}
