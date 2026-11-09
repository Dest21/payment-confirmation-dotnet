using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Payments.Data;

public class FudisDbContext(DbContextOptions<FudisDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<DriverEarning> DriverEarnings => Set<DriverEarning>();
    public DbSet<PaymentNotificationRecord> PaymentNotifications => Set<PaymentNotificationRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(e =>
        {
            e.Property(o => o.Status).HasMaxLength(30).IsUnicode(false);
            e.Property(o => o.DeliveryFee).HasPrecision(10, 2);
            e.Property(o => o.Total).HasPrecision(10, 2);
            e.Property(o => o.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            e.Property(o => o.PaidPaymentId).HasMaxLength(100).IsUnicode(false);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Orders_Status",
                    $"[Status] IN ('{OrderStatus.PendingPayment}', '{OrderStatus.Paid}', '{OrderStatus.Cancelled}')");
                t.HasCheckConstraint("CK_Orders_Amounts", "[DeliveryFee] >= 0 AND [Total] >= 0");
            });
        });

        modelBuilder.Entity<DriverEarning>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(10, 2);
            e.Property(x => x.Rate).HasPrecision(5, 4);
            e.Property(x => x.Status).HasMaxLength(30).IsUnicode(false);
            // Una sola ganancia por pedido: la última defensa contra el doble pago,
            // aunque la lógica de la aplicación falle.
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentNotificationRecord>(e =>
        {
            e.ToTable("PaymentNotifications");
            e.Property(x => x.ProviderEventId).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.PaymentId).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.Amount).HasPrecision(10, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            e.Property(x => x.Outcome).HasMaxLength(30).IsUnicode(false);
            e.HasIndex(x => x.ProviderEventId).IsUnique();
            e.HasIndex(x => x.OrderId);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.MessageId).IsUnique();
            e.HasIndex(x => x.NextAttemptAt).HasFilter("[ProcessedAt] IS NULL AND [FailedAt] IS NULL");
        });
    }
}
