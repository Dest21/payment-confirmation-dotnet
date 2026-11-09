using Fudis.Payments.Data;
using Fudis.Payments.Outbox;
using Fudis.Payments.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fudis.Payments;

public static class PaymentsServiceCollectionExtensions
{
    /// <summary>Registra la confirmación de pagos, la persistencia y el outbox.</summary>
    public static IServiceCollection AddPayments(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<FudisDbContext>(o => o.UseSqlServer(configuration.GetConnectionString("Fudis")));

        services.AddOptions<PaymentOptions>()
            .Bind(configuration.GetSection(PaymentOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PaymentOptions>, PaymentOptionsValidator>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SignatureVerifier>();
        services.AddScoped<ConfirmPaymentHandler>();

        services.AddScoped<OutboxProcessor>();
        services.AddScoped<IOutboxHandler, RestaurantPaymentConfirmedHandler>();
        services.AddScoped<IOutboxHandler, PaymentReviewRequestedHandler>();
        services.AddSingleton<IPaymentReviewAlerter, LoggingReviewAlerter>();
        services.AddHostedService<OutboxWorker>();

        return services;
    }
}
