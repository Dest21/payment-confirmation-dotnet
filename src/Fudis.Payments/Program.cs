using Fudis.Payments;
using Fudis.Payments.Demo;
using Fudis.Payments.Outbox;

var builder = WebApplication.CreateBuilder(args);
// La demo se activa por configuración (Demo:Enabled), nunca por defecto.
var isDemo = builder.Configuration.GetValue<bool>("Demo:Enabled");

builder.Services.AddControllers();
builder.Services.AddPayments(builder.Configuration);

if (isDemo)
    builder.Services.AddPaymentsDemo();   // restaurante simulado y simulador de la pasarela
else
    builder.Services.AddSingleton<IRestaurantNotifier, LoggingRestaurantNotifier>();

var app = builder.Build();

if (isDemo)
{
    await DemoController.PrepareDatabaseAsync(app.Services);
    app.UseRateLimiter();
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapControllers();
await app.RunAsync();

public partial class Program;
