using Fudis.Payments.Domain;
using Fudis.Payments.Outbox;
using Fudis.Payments.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fudis.Payments.Tests.Integration;

public class OutboxTests
{
    private sealed class FlakyNotifier(int failuresBeforeSuccess) : IRestaurantNotifier
    {
        public int Calls { get; private set; }

        public Task NotifyPaymentConfirmedAsync(int orderId, Guid messageId, CancellationToken ct)
        {
            Calls++;
            if (Calls <= failuresBeforeSuccess) throw new HttpRequestException("servicio del restaurante caído");
            return Task.CompletedTask;
        }
    }

    private sealed class NoAlerts : IPaymentReviewAlerter
    {
        public Task AlertAsync(PaymentReviewRequested review, Guid messageId, CancellationToken ct) => Task.CompletedTask;
    }

    private static OutboxProcessor Processor(TestDb db, Fudis.Payments.Data.FudisDbContext ctx, IRestaurantNotifier notifier) =>
        new(ctx,
            [new RestaurantPaymentConfirmedHandler(notifier), new PaymentReviewRequestedHandler(new NoAlerts())],
            db.Clock,
            NullLogger<OutboxProcessor>.Instance);

    [Fact]
    public async Task Failed_notification_keeps_payment_and_is_retried_after_backoff()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();
        await using (var ctx = db.NewContext())
            await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id));

        var notifier = new FlakyNotifier(failuresBeforeSuccess: 1);

        // 1er intento: falla. El pago y la ganancia siguen guardados.
        await using (var ctx = db.NewContext())
            await Processor(db, ctx, notifier).ProcessBatchAsync();
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
            Assert.Equal(0, await Processor(db, ctx, notifier).ProcessBatchAsync());

        // Pasada la espera: se reintenta y se marca como enviado.
        db.Clock.Advance(TimeSpan.FromSeconds(3));
        await using (var ctx = db.NewContext())
            await Processor(db, ctx, notifier).ProcessBatchAsync();
        await using (var check = db.NewContext())
            Assert.NotNull((await check.OutboxMessages.SingleAsync()).ProcessedAt);
        Assert.Equal(2, notifier.Calls);
    }

    [Fact]
    public async Task Message_is_marked_failed_after_max_attempts()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();
        await using (var ctx = db.NewContext())
            await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id));

        var notifier = new FlakyNotifier(failuresBeforeSuccess: int.MaxValue);
        for (var i = 0; i < OutboxProcessor.MaxAttempts; i++)
        {
            await using var ctx = db.NewContext();
            await Processor(db, ctx, notifier).ProcessBatchAsync();
            db.Clock.Advance(TimeSpan.FromMinutes(11));
        }

        await using var check = db.NewContext();
        var m = await check.OutboxMessages.SingleAsync();
        Assert.NotNull(m.FailedAt);
        Assert.Equal(OutboxProcessor.MaxAttempts, m.Attempts);
    }

    [Fact]
    public async Task Unknown_message_type_is_retried_instead_of_lost()
    {
        await using var db = await TestDb.CreateAsync();
        await using (var ctx = db.NewContext())
        {
            ctx.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid(), Type = "TipoQueNoExiste", Payload = "{}",
                CreatedAt = db.Clock.GetUtcNow().UtcDateTime, NextAttemptAt = db.Clock.GetUtcNow().UtcDateTime,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.NewContext())
            await Processor(db, ctx, new FlakyNotifier(0)).ProcessBatchAsync();

        await using var check = db.NewContext();
        var m = await check.OutboxMessages.SingleAsync();
        Assert.Null(m.ProcessedAt);
        Assert.Equal(1, m.Attempts);
        Assert.Contains("TipoQueNoExiste", m.LastError, StringComparison.Ordinal);
    }
}
