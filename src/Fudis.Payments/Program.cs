using Fudis.Payments.Data;
using Fudis.Payments.Outbox;
using Fudis.Payments.Payments;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<FudisDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Fudis")));
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payments"));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<ConfirmPaymentHandler>();
builder.Services.AddSingleton<SignatureVerifier>();
builder.Services.AddScoped<OutboxProcessor>();
builder.Services.AddSingleton<IRestaurantNotifier, LoggingRestaurantNotifier>();
builder.Services.AddSingleton<IPaymentReviewAlerter, LoggingReviewAlerter>();
builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();
app.MapControllers();
app.Run();

public partial class Program;
