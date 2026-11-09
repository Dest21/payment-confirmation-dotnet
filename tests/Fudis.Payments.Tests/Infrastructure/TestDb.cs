using Fudis.Payments.Data;
using Fudis.Payments.Domain;
using Fudis.Payments.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fudis.Payments.Tests.Infrastructure;

/// <summary>
/// Una base SQL Server real y nueva por prueba. Las garantías dependen de los bloqueos
/// e índices únicos de SQL Server, así que no se usa una base en memoria.
/// Requiere FUDIS_TEST_SQL (cadena de conexión al servidor, sin base).
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    private readonly string _connectionString;
    public string ConnectionString => _connectionString;
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero));
    public IOptions<PaymentOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(
        new PaymentOptions { WebhookSecret = "test-secret", DriverShare = 0.70m });

    private TestDb(string connectionString) => _connectionString = connectionString;

    public static async Task<TestDb> CreateAsync()
    {
        var server = Environment.GetEnvironmentVariable("FUDIS_TEST_SQL")
                     ?? throw new InvalidOperationException("Falta FUDIS_TEST_SQL");
        var db = new TestDb($"{server};Database=fudis_test_{Guid.NewGuid():N}");
        await using var ctx = db.NewContext();
        await ctx.Database.EnsureCreatedAsync();
        return db;
    }

    public FudisDbContext NewContext() =>
        new(new DbContextOptionsBuilder<FudisDbContext>().UseSqlServer(_connectionString).Options);

    public ConfirmPaymentHandler NewHandler(FudisDbContext ctx) => new(ctx, Options, Clock);

    public async Task<Order> SeedOrderAsync(decimal deliveryFee = 10m, decimal total = 50m, int? driverId = 7,
        string status = OrderStatus.PendingPayment)
    {
        await using var ctx = NewContext();
        var order = new Order { DeliveryFee = deliveryFee, Total = total, Currency = "PEN", DriverId = driverId, Status = status };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return order;
    }

    public static PaymentNotification Approved(int orderId, decimal amount = 50m, string? eventId = null, string paymentId = "pay_1") =>
        new(eventId ?? $"evt_{Guid.NewGuid():N}", paymentId, orderId, amount, "PEN", PaymentNotification.Approved,
            DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.EnsureDeletedAsync();
    }
}

public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
