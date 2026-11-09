using Fudis.Payments.Data;
using Fudis.Payments.Demo;
using Fudis.Payments.Outbox;
using Fudis.Payments.Payments;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var isDemo = builder.Environment.IsDevelopment();

builder.Services.AddControllers();
builder.Services.AddDbContext<FudisDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Fudis")));
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payments"));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<ConfirmPaymentHandler>();
builder.Services.AddSingleton<SignatureVerifier>();
builder.Services.AddScoped<OutboxProcessor>();
builder.Services.AddSingleton<IPaymentReviewAlerter, LoggingReviewAlerter>();
builder.Services.AddHostedService<OutboxWorker>();

if (isDemo)
{
    // Demo (solo Development): restaurante simulado que se puede "caer" y cliente HTTP
    // para que el simulador de la pasarela llame al endpoint real.
    builder.Services.AddSingleton<DemoRestaurantNotifier>();
    builder.Services.AddSingleton<IRestaurantNotifier>(sp => sp.GetRequiredService<DemoRestaurantNotifier>());
    builder.Services.AddHttpClient();
}
else
{
    builder.Services.AddSingleton<IRestaurantNotifier, LoggingRestaurantNotifier>();
}

var app = builder.Build();

if (isDemo)
{
    await DemoController.PrepareDatabaseAsync(app.Services);
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapControllers();
app.Run();

public partial class Program;
