using Microsoft.Extensions.Options;

namespace Fudis.Payments.Payments;

public sealed class PaymentOptions
{
    public const string Section = "Payments";

    /// <summary>Secreto compartido con la pasarela para firmar las notificaciones (HMAC-SHA256).</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Antigüedad máxima aceptada de una notificación firmada, contra ataques de repetición.</summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    /// <summary>Parte de la tarifa de envío que gana el repartidor.</summary>
    public decimal DriverShare { get; set; } = 0.70m;
}

/// <summary>
/// Se valida al arrancar: sin secreto o con un porcentaje fuera de rango la aplicación no
/// inicia, en vez de fallar con el primer pago real.
/// </summary>
public sealed class PaymentOptionsValidator : IValidateOptions<PaymentOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
            errors.Add($"{PaymentOptions.Section}:WebhookSecret es obligatorio.");
        if (options.SignatureToleranceSeconds is < 30 or > 3600)
            errors.Add($"{PaymentOptions.Section}:SignatureToleranceSeconds debe estar entre 30 y 3600.");
        if (options.DriverShare is <= 0 or > 1)
            errors.Add($"{PaymentOptions.Section}:DriverShare debe estar entre 0 y 1.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
