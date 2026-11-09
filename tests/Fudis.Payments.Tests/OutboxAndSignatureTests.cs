using System.Net;
using System.Text;
using Fudis.Payments.Domain;
using Fudis.Payments.Outbox;
using Fudis.Payments.Payments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fudis.Payments.Tests;

public class OutboxTests
{
    private sealed class FlakyNotifier(int failuresBeforeSuccess) : IRestaurantNotifier
    {
        public int Calls;
        public Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct)
        {
            Calls++;
            if (Calls <= failuresBeforeSuccess) throw new HttpRequestException("servicio del restaurante caído");
            return Task.CompletedTask;
        }
    }

    private sealed class NoAlerts : IPaymentReviewAlerter
    {
        public Task AlertAsync(string payload, Guid messageId, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Si_el_aviso_falla_el_pago_se_mantiene_y_el_aviso_se_reintenta()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();
        await using (var ctx = db.NewContext())
            await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id));

        var notifier = new FlakyNotifier(failuresBeforeSuccess: 1);

        // 1er intento: falla. El pago y la ganancia siguen guardados.
        await using (var ctx = db.NewContext())
            await new OutboxProcessor(ctx, notifier, new NoAlerts(), db.Clock, NullLogger<OutboxProcessor>.Instance).ProcessBatchAsync();
        await using (var check = db.NewContext())
        {
            var m = await check.OutboxMessages.SingleAsync();
            Assert.Null(m.ProcessedAt);
            Assert.Equal(1, m.Attempts);
            Assert.Equal(OrderStatus.Paid, (await check.Orders.SingleAsync()).Status);
            Assert.Equal(1, await check.DriverEarnings.CountAsync());
        }

        // Antes de la espera no se reintenta.
        await using (var ctx = db.NewContext())
            Assert.Equal(0, await new OutboxProcessor(ctx, notifier, new NoAlerts(), db.Clock, NullLogger<OutboxProcessor>.Instance).ProcessBatchAsync());

        // Pasada la espera: se reintenta y se marca como enviado.
        db.Clock.Advance(TimeSpan.FromSeconds(3));
        await using (var ctx = db.NewContext())
            await new OutboxProcessor(ctx, notifier, new NoAlerts(), db.Clock, NullLogger<OutboxProcessor>.Instance).ProcessBatchAsync();
        await using (var check = db.NewContext())
            Assert.NotNull((await check.OutboxMessages.SingleAsync()).ProcessedAt);
        Assert.Equal(2, notifier.Calls);
    }

    [Fact]
    public async Task Tras_agotar_los_reintentos_el_mensaje_queda_como_fallido()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();
        await using (var ctx = db.NewContext())
            await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id));

        var notifier = new FlakyNotifier(failuresBeforeSuccess: int.MaxValue);
        for (var i = 0; i < OutboxProcessor.MaxAttempts; i++)
        {
            await using var ctx = db.NewContext();
            await new OutboxProcessor(ctx, notifier, new NoAlerts(), db.Clock, NullLogger<OutboxProcessor>.Instance).ProcessBatchAsync();
            db.Clock.Advance(TimeSpan.FromMinutes(11));
        }

        await using var check = db.NewContext();
        var m = await check.OutboxMessages.SingleAsync();
        Assert.NotNull(m.FailedAt);
        Assert.Equal(OutboxProcessor.MaxAttempts, m.Attempts);
    }
}

public class SignatureTests
{
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero));
    private static SignatureVerifier Verifier() => new(
        Microsoft.Extensions.Options.Options.Create(new PaymentOptions { WebhookSecret = "test-secret" }), Clock);

    private static string Ts(int offsetSeconds = 0) => (Clock.GetUtcNow().ToUnixTimeSeconds() + offsetSeconds).ToString();
    private static string Sig(string ts, string body, string secret = "test-secret") =>
        Convert.ToHexString(SignatureVerifier.Sign(secret, ts, body));

    [Fact] public void Firma_correcta_es_valida() => Assert.True(Verifier().IsValid("{}", Ts(), Sig(Ts(), "{}")));
    [Fact] public void Cuerpo_alterado_es_invalido() => Assert.False(Verifier().IsValid("{\"amount\":1}", Ts(), Sig(Ts(), "{}")));
    [Fact] public void Otro_secreto_es_invalido() => Assert.False(Verifier().IsValid("{}", Ts(), Sig(Ts(), "{}", "otro")));
    [Fact] public void Sin_firma_es_invalido() => Assert.False(Verifier().IsValid("{}", Ts(), null));
    [Fact] public void Firma_vieja_es_invalida() => Assert.False(Verifier().IsValid("{}", Ts(-600), Sig(Ts(-600), "{}")));
    [Fact] public void Firma_no_hexadecimal_es_invalida() => Assert.False(Verifier().IsValid("{}", Ts(), "zz"));
}

/// <summary>Prueba de punta a punta por HTTP: firma, contrato y códigos de respuesta.</summary>
public class EndpointTests
{
    [Fact]
    public async Task Endpoint_rechaza_firma_invalida_y_acepta_la_valida_de_forma_idempotente()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Fudis", db.ConnectionString);
            b.UseSetting("Payments:WebhookSecret", "test-secret");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IHostedService>(); // el outbox se prueba por separado
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(db.Clock);
            });
        });
        var client = factory.CreateClient();

        var body = $$"""{"eventId":"evt_http","paymentId":"pay_1","orderId":{{order.Id}},"amount":50.00,"currency":"PEN","status":"APPROVED","occurredAt":"2026-11-09T15:00:00Z"}""";
        var ts = db.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();

        HttpRequestMessage Request(string signature)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/payments/confirm-payment")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            r.Headers.Add("X-Fudis-Timestamp", ts);
            r.Headers.Add("X-Fudis-Signature", signature);
            return r;
        }

        var bad = await client.SendAsync(Request("00"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var good = Convert.ToHexString(SignatureVerifier.Sign("test-secret", ts, body));
        var first = await client.SendAsync(Request(good));
        var second = await client.SendAsync(Request(good));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("Applied", await first.Content.ReadAsStringAsync());
        Assert.Contains("Duplicate", await second.Content.ReadAsStringAsync());

        await using var check = db.NewContext();
        Assert.Equal(1, await check.DriverEarnings.CountAsync());
    }
}
