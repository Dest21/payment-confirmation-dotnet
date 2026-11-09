using Fudis.Payments.Domain;
using Xunit;

namespace Fudis.Payments.Tests.Unit;

/// <summary>Reglas de negocio puras: sin base de datos, corren en milisegundos.</summary>
public class DriverEarningPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(10, 7.00)]
    [InlineData(7.15, 5.01)]   // 5.005 → 5.01: mitad hacia arriba, no redondeo bancario (que daría 5.00)
    [InlineData(4.99, 3.49)]   // 3.493 → 3.49
    [InlineData(0, 0)]
    public void Earning_is_70_percent_rounded_to_cents(decimal fee, decimal expected)
    {
        var earning = DriverEarningPolicy.For(new Order { Id = 1, DeliveryFee = fee, DriverId = 7 }, 0.70m, Now);

        Assert.Equal(expected, earning.Amount);
        Assert.Equal(0.70m, earning.Rate);
        Assert.Equal(EarningStatus.Payable, earning.Status);
    }

    [Fact]
    public void Order_without_driver_gets_pending_assignment()
    {
        var earning = DriverEarningPolicy.For(new Order { Id = 1, DeliveryFee = 10m, DriverId = null }, 0.70m, Now);

        Assert.Null(earning.DriverId);
        Assert.Equal(EarningStatus.PendingAssignment, earning.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Share_outside_0_to_1_is_rejected(decimal share) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DriverEarningPolicy.For(new Order(), share, Now));
}

public class PaymentRulesTests
{
    private static OrderSnapshot Order(string status, string? paidWith = null) => new(status, 50m, "PEN", paidWith);

    [Fact]
    public void Retry_of_the_same_charge_needs_no_review()
    {
        var d = PaymentRules.ExplainNotApplied(Order(OrderStatus.Paid, "pay_1"), "pay_1", 50m, "PEN");

        Assert.Equal(NotificationOutcome.AlreadyPaid, d.Outcome);
        Assert.Null(d.ReviewReason);
    }

    [Fact]
    public void Different_charge_for_paid_order_is_flagged_as_double_charge()
    {
        var d = PaymentRules.ExplainNotApplied(Order(OrderStatus.Paid, "pay_1"), "pay_2", 50m, "PEN");

        Assert.Equal(NotificationOutcome.AlreadyPaid, d.Outcome);
        Assert.Contains("doble cargo", d.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_order_that_was_not_updated_means_amount_mismatch()
    {
        var d = PaymentRules.ExplainNotApplied(Order(OrderStatus.PendingPayment), "pay_1", 1m, "PEN");

        Assert.Equal(NotificationOutcome.AmountMismatch, d.Outcome);
        Assert.Equal("Importe recibido 1.00 PEN, esperado 50.00 PEN.", d.ReviewReason);
    }

    [Fact]
    public void Cancelled_order_is_not_payable_and_needs_refund_review()
    {
        var d = PaymentRules.ExplainNotApplied(Order(OrderStatus.Cancelled), "pay_1", 50m, "PEN");

        Assert.Equal(NotificationOutcome.OrderNotPayable, d.Outcome);
        Assert.Contains("reembolso", d.ReviewReason, StringComparison.Ordinal);
    }
}
