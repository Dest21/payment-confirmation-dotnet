using Fudis.Payments.Domain;
using Fudis.Payments.Payments;
using Microsoft.EntityFrameworkCore;
using Fudis.Payments.Tests.Infrastructure;
using Xunit;

namespace Fudis.Payments.Tests.Integration;

/// <summary>Garantías del caso de uso contra un SQL Server real (bloqueos, índices únicos, transacciones).</summary>
public class ConfirmPaymentTests
{
    [Fact]
    public async Task Pending_order_becomes_paid_with_70_percent_earning_and_outbox_notice()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync(deliveryFee: 10m);

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.Applied, await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id)));

        await using var check = db.NewContext();
        var saved = await check.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Paid, saved.Status);
        Assert.NotNull(saved.PaidAt);
        var earning = await check.DriverEarnings.SingleAsync();
        Assert.Equal(7.00m, earning.Amount);
        Assert.Equal(7, earning.DriverId);
        Assert.Equal(EarningStatus.Payable, earning.Status);
        var message = await check.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(RestaurantPaymentConfirmed), message.Type);
    }

    [Fact]
    public async Task Same_notification_twice_has_no_extra_effect()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();
        var n = TestDb.Approved(order.Id, eventId: "evt_repetido");

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.Applied, await db.NewHandler(ctx).HandleAsync(n));
        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.Duplicate, await db.NewHandler(ctx).HandleAsync(n));

        await using var check = db.NewContext();
        Assert.Equal(1, await check.DriverEarnings.CountAsync());
        Assert.Equal(1, await check.OutboxMessages.CountAsync());
        Assert.Equal(1, await check.PaymentNotifications.CountAsync());
    }

    [Fact]
    public async Task Same_notification_20_times_in_parallel_pays_once()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();
        var n = TestDb.Approved(order.Id, eventId: "evt_paralelo");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var ctx = db.NewContext();
            return await db.NewHandler(ctx).HandleAsync(n);
        }));

        Assert.Single(results, r => r == ConfirmResult.Applied);
        Assert.All(results, r => Assert.Contains(r, new[] { ConfirmResult.Applied, ConfirmResult.Duplicate }));
        await using var check = db.NewContext();
        Assert.Equal(1, await check.DriverEarnings.CountAsync());
        Assert.Equal(1, await check.OutboxMessages.CountAsync(m => m.Type == nameof(RestaurantPaymentConfirmed)));
    }

    [Fact]
    public async Task Different_event_ids_for_same_payment_in_parallel_pay_once()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();

        // La pasarela reintenta con un EventId nuevo pero el mismo cobro (PaymentId).
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            await using var ctx = db.NewContext();
            return await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id, eventId: $"evt_{i}", paymentId: "pay_1"));
        }));

        Assert.Single(results, r => r == ConfirmResult.Applied);
        Assert.Equal(9, results.Count(r => r == ConfirmResult.AlreadyPaid));
        await using var check = db.NewContext();
        Assert.Equal(1, await check.DriverEarnings.CountAsync());
        Assert.Equal(0, await check.OutboxMessages.CountAsync(m => m.Type == nameof(PaymentReviewRequested)));
    }

    [Fact]
    public async Task Second_distinct_charge_for_paid_order_raises_double_charge_alert()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();

        await using (var ctx = db.NewContext())
            await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id, paymentId: "pay_1"));
        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.AlreadyPaid,
                await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id, paymentId: "pay_2")));

        await using var check = db.NewContext();
        Assert.Equal(1, await check.DriverEarnings.CountAsync());
        Assert.Equal(1, await check.OutboxMessages.CountAsync(m => m.Type == nameof(PaymentReviewRequested)));
    }

    [Fact]
    public async Task Amount_mismatch_keeps_order_pending_and_requests_review()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync(total: 50m);

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.AmountMismatch,
                await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id, amount: 1m)));

        await using var check = db.NewContext();
        Assert.Equal(OrderStatus.PendingPayment, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(0, await check.DriverEarnings.CountAsync());
        Assert.Equal(NotificationOutcome.AmountMismatch, (await check.PaymentNotifications.SingleAsync()).Outcome);
        Assert.Equal(nameof(PaymentReviewRequested), (await check.OutboxMessages.SingleAsync()).Type);
    }

    [Fact]
    public async Task Cancelled_order_is_not_paid_and_has_no_earning()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync(status: OrderStatus.Cancelled);

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.OrderNotPayable, await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id)));

        await using var check = db.NewContext();
        Assert.Equal(OrderStatus.Cancelled, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(0, await check.DriverEarnings.CountAsync());
        Assert.Equal(nameof(PaymentReviewRequested), (await check.OutboxMessages.SingleAsync()).Type);
    }

    [Fact]
    public async Task Unknown_order_leaves_no_trace_so_gateway_retries()
    {
        await using var db = await TestDb.CreateAsync();

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.OrderNotFound, await db.NewHandler(ctx).HandleAsync(TestDb.Approved(999)));

        await using var check = db.NewContext();
        Assert.Equal(0, await check.PaymentNotifications.CountAsync());
    }

    [Fact]
    public async Task Order_without_driver_gets_pending_assignment_earning()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync(driverId: null);

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.Applied, await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id)));

        await using var check = db.NewContext();
        var earning = await check.DriverEarnings.SingleAsync();
        Assert.Null(earning.DriverId);
        Assert.Equal(EarningStatus.PendingAssignment, earning.Status);
    }

    [Fact]
    public async Task Database_rejects_second_earning_for_same_order()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync();

        await using var ctx = db.NewContext();
        ctx.DriverEarnings.Add(new DriverEarning { OrderId = order.Id, DriverId = 7, Amount = 7m, Rate = 0.7m });
        ctx.DriverEarnings.Add(new DriverEarning { OrderId = order.Id, DriverId = 7, Amount = 7m, Rate = 0.7m });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.True(Fudis.Payments.Data.SqlErrors.IsUniqueViolation(ex));
    }
}
