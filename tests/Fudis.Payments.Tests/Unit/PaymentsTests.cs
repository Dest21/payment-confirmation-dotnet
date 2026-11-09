using System.Globalization;
using Fudis.Payments.Payments;
using Fudis.Payments.Tests.Infrastructure;
using Xunit;

namespace Fudis.Payments.Tests.Unit;

public class SignatureVerifierTests
{
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero));

    private static SignatureVerifier Verifier() => new(
        Microsoft.Extensions.Options.Options.Create(new PaymentOptions { WebhookSecret = "test-secret" }), Clock);

    private static string Ts(int offsetSeconds = 0) =>
        (Clock.GetUtcNow().ToUnixTimeSeconds() + offsetSeconds).ToString(CultureInfo.InvariantCulture);

    private static string Sig(string ts, string body, string secret = "test-secret") =>
        Convert.ToHexString(SignatureVerifier.Sign(secret, ts, body));

    [Fact] public void Valid_signature_is_accepted() => Assert.True(Verifier().IsValid("{}", Ts(), Sig(Ts(), "{}")));
    [Fact] public void Lowercase_hex_signature_is_accepted() => Assert.True(Verifier().IsValid("{}", Ts(), Sig(Ts(), "{}").ToLowerInvariant()));
    [Fact] public void Tampered_body_is_rejected() => Assert.False(Verifier().IsValid("{\"amount\":1}", Ts(), Sig(Ts(), "{}")));
    [Fact] public void Wrong_secret_is_rejected() => Assert.False(Verifier().IsValid("{}", Ts(), Sig(Ts(), "{}", "otro")));
    [Fact] public void Missing_signature_is_rejected() => Assert.False(Verifier().IsValid("{}", Ts(), null));
    [Fact] public void Expired_timestamp_is_rejected() => Assert.False(Verifier().IsValid("{}", Ts(-600), Sig(Ts(-600), "{}")));
    [Fact] public void Non_hex_signature_is_rejected() => Assert.False(Verifier().IsValid("{}", Ts(), "zz"));
}

public class PaymentNotificationTests
{
    private static PaymentNotification Valid() =>
        new("evt_1", "pay_1", 10, 50.00m, "PEN", PaymentNotification.Approved, DateTimeOffset.UtcNow);

    [Fact]
    public void Valid_notification_has_no_errors() => Assert.Empty(Valid().Validate());

    [Fact]
    public void Missing_fields_and_bad_amounts_are_reported()
    {
        var errors = (Valid() with { EventId = "", OrderId = 0, Amount = 10.001m, Currency = "SOLES" }).Validate();

        Assert.Equal(4, errors.Count);
    }
}

public class PaymentOptionsValidatorTests
{
    [Fact]
    public void Valid_options_pass() =>
        Assert.True(new PaymentOptionsValidator().Validate(null, new PaymentOptions { WebhookSecret = "s" }).Succeeded);

    [Fact]
    public void Missing_secret_and_bad_share_fail()
    {
        var result = new PaymentOptionsValidator().Validate(null, new PaymentOptions { WebhookSecret = "", DriverShare = 1.5m });

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
    }
}
