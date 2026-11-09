using System.Globalization;
using System.Net;
using System.Text;
using Fudis.Payments.Payments;
using Fudis.Payments.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fudis.Payments.Tests.Integration;

/// <summary>De punta a punta por HTTP: firma, contrato, códigos de respuesta e idempotencia.</summary>
public class EndpointTests
{
    private const string Secret = "test-secret";

    private static WebApplicationFactory<Program> Api(TestDb db) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production"); // sin demo
            b.UseSetting("ConnectionStrings:Fudis", db.ConnectionString);
            b.UseSetting("Payments:WebhookSecret", Secret);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IHostedService>(); // el outbox se prueba por separado
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(db.Clock);
            });
        });

    private static HttpRequestMessage Signed(string body, string timestamp, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments/confirm-payment")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(PaymentsController.TimestampHeader, timestamp);
        request.Headers.Add(PaymentsController.SignatureHeader,
            signature ?? Convert.ToHexString(SignatureVerifier.Sign(Secret, timestamp, body)));
        return request;
    }

    [Fact]
    public async Task Endpoint_rejects_bad_signature_and_accepts_valid_one_idempotently()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();
        await using var factory = Api(db);
        var client = factory.CreateClient();

        var body = $$"""{"eventId":"evt_http","paymentId":"pay_1","orderId":{{order.Id}},"amount":50.00,"currency":"PEN","status":"APPROVED","occurredAt":"2026-11-09T15:00:00Z"}""";
        var ts = db.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var forged = await client.SendAsync(Signed(body, ts, signature: "00"));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        using var first = await client.SendAsync(Signed(body, ts));
        using var second = await client.SendAsync(Signed(body, ts));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("Applied", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Duplicate", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var check = db.NewContext();
        Assert.Equal(1, await check.DriverEarnings.CountAsync());
    }

    [Fact]
    public async Task Invalid_contract_is_rejected_with_400_before_touching_the_database()
    {
        await using var db = await TestDb.CreateAsync();
        await using var factory = Api(db);
        var client = factory.CreateClient();
        var ts = db.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var res = await client.SendAsync(Signed("""{"eventId":"","orderId":0,"amount":-1}""", ts));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        await using var check = db.NewContext();
        Assert.Equal(0, await check.PaymentNotifications.CountAsync());
    }

    [Fact]
    public void App_does_not_start_without_webhook_secret()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Payments:WebhookSecret", "");
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("WebhookSecret", ex.ToString(), StringComparison.Ordinal);
    }
}
