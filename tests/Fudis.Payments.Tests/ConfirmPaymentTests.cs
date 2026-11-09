using Fudis.Payments.Domain;
using Fudis.Payments.Payments;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fudis.Payments.Tests;

public class ConfirmPaymentTests
{
    [Fact]
    public async Task Pedido_pendiente_queda_pagado_con_ganancia_del_70_y_aviso_en_outbox()
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
        Assert.Equal(OutboxMessageType.RestaurantPaymentConfirmed, message.Type);
    }

    [Fact]
    public async Task Misma_notificacion_dos_veces_no_duplica_nada()
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
    public async Task Misma_notificacion_20_veces_en_paralelo_paga_una_sola_vez()
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
        Assert.Equal(1, await check.OutboxMessages.CountAsync(m => m.Type == OutboxMessageType.RestaurantPaymentConfirmed));
    }

    [Fact]
    public async Task Notificaciones_distintas_del_mismo_pago_en_paralelo_pagan_una_sola_vez()
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
        Assert.Equal(0, await check.OutboxMessages.CountAsync(m => m.Type == OutboxMessageType.PaymentRequiresReview));
    }

    [Fact]
    public async Task Segundo_cobro_distinto_para_pedido_pagado_genera_alerta_de_doble_cargo()
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
        Assert.Equal(1, await check.OutboxMessages.CountAsync(m => m.Type == OutboxMessageType.PaymentRequiresReview));
    }

    [Fact]
    public async Task Importe_distinto_no_marca_pagado_y_pide_revision()
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
        Assert.Equal(OutboxMessageType.PaymentRequiresReview, (await check.OutboxMessages.SingleAsync()).Type);
    }

    [Fact]
    public async Task Pedido_cancelado_no_se_paga_ni_genera_ganancia()
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync(status: OrderStatus.Cancelled);

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.OrderNotPayable, await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id)));

        await using var check = db.NewContext();
        Assert.Equal(OrderStatus.Cancelled, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(0, await check.DriverEarnings.CountAsync());
        Assert.Equal(OutboxMessageType.PaymentRequiresReview, (await check.OutboxMessages.SingleAsync()).Type);
    }

    [Fact]
    public async Task Pedido_inexistente_no_deja_rastro_para_que_la_pasarela_reintente()
    {
        await using var db = await TestDb.CreateAsync();

        await using (var ctx = db.NewContext())
            Assert.Equal(ConfirmResult.OrderNotFound, await db.NewHandler(ctx).HandleAsync(TestDb.Approved(999)));

        await using var check = db.NewContext();
        Assert.Equal(0, await check.PaymentNotifications.CountAsync());
    }

    [Fact]
    public async Task Pedido_sin_repartidor_deja_la_ganancia_pendiente_de_asignacion()
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

    [Theory]
    [InlineData(7.15, 5.01)]   // 5.005 → 5.01 (mitad hacia arriba, no redondeo bancario)
    [InlineData(4.99, 3.49)]   // 3.493 → 3.49
    [InlineData(0, 0)]
    public async Task Ganancia_se_redondea_a_centimos(decimal fee, decimal expected)
    {
        await using var db = await TestDb.CreateAsync();
        var order = await db.SeedOrderAsync(deliveryFee: fee);

        await using (var ctx = db.NewContext())
            await db.NewHandler(ctx).HandleAsync(TestDb.Approved(order.Id));

        await using var check = db.NewContext();
        Assert.Equal(expected, (await check.DriverEarnings.SingleAsync()).Amount);
    }

    [Fact]
    public async Task La_base_rechaza_una_segunda_ganancia_para_el_mismo_pedido()
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
