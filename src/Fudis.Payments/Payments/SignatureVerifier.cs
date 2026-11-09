using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Fudis.Payments.Payments;

/// <summary>
/// Verifica que la notificación venga de la pasarela: firma = HMAC-SHA256(secreto, "{timestamp}.{cuerpo}")
/// en hexadecimal. Incluir el timestamp limita las repeticiones de una notificación capturada.
/// </summary>
public sealed class SignatureVerifier(IOptions<PaymentOptions> options, TimeProvider clock)
{
    public bool IsValid(string rawBody, string? timestampHeader, string? signatureHeader)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(timestampHeader) || string.IsNullOrEmpty(signatureHeader))
            return false;

        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            return false;
        var age = clock.GetUtcNow().ToUnixTimeSeconds() - unix;
        if (Math.Abs(age) > options.Value.SignatureToleranceSeconds)
            return false;

        byte[] given;
        try { given = Convert.FromHexString(signatureHeader); }
        catch (FormatException) { return false; }

        var expected = Sign(secret, timestampHeader, rawBody);
        // Comparación en tiempo constante: no filtra cuántos bytes coinciden.
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public static byte[] Sign(string secret, string timestamp, string rawBody) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}"));
}
