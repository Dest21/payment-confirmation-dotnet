using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Fudis.Payments.Data;
using Fudis.Payments.Domain;
using Fudis.Payments.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fudis.Payments.Demo;

/// <summary>Solo responde en Development: la demo nunca se expone en producción.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DemoOnlyAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

/// <summary>
/// Simulador de la pasarela y del código original, para mostrar en vivo cómo se comporta
/// cada versión. La notificación de la "solución" pasa por el endpoint real, con firma.
/// </summary>
[ApiController]
[Route("demo")]
[DemoOnly]
public sealed class DemoController(
    FudisDbContext db,
    IHttpClientFactory http,
    IOptions<PaymentOptions> options,
    DemoRestaurantNotifier restaurant,
    IConfiguration config) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record NewOrder(decimal DeliveryFee = 10m, decimal Total = 50m, int? DriverId = 7,
        string Status = OrderStatus.PendingPayment);

    [HttpPost("orders")]
    public async Task<Order> CreateOrder(NewOrder o)
    {
        var order = new Order { DeliveryFee = o.DeliveryFee, Total = o.Total, Currency = "PEN", DriverId = o.DriverId, Status = o.Status };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    public sealed record SendRequest(
        int OrderId,
        string Mode = "solution",       // "solution" o "original"
        int Copies = 1,                 // cuántas notificaciones a la vez
        bool SameEventId = true,        // false: la pasarela regenera el EventId en cada reintento
        string? EventId = null,
        string PaymentId = "pay_1",
        decimal? Amount = null,
        bool ValidSignature = true,
        int LatencyMs = 0);             // solo afecta al código original (ver OriginalConfirmPayment)

    public sealed record SendResult(string EventId, int Status, string Body, long Ms);

    [HttpPost("send")]
    public async Task<IReadOnlyList<SendResult>> Send(SendRequest r)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == r.OrderId);
        var baseEvent = r.EventId ?? $"evt_{Guid.NewGuid():N}"[..16];
        var client = http.CreateClient();
        client.BaseAddress = new Uri(config["Demo:SelfUrl"] ?? $"{Request.Scheme}://{Request.Host}");

        // Todas las peticiones salen a la vez: eso es lo que provoca la carrera.
        var tasks = Enumerable.Range(0, Math.Clamp(r.Copies, 1, 50)).Select(async i =>
        {
            var eventId = r.SameEventId ? baseEvent : $"{baseEvent}_{i}";
            var body = JsonSerializer.Serialize(new PaymentNotification(
                eventId, r.PaymentId, order.Id, r.Amount ?? order.Total, order.Currency.Trim(),
                PaymentNotification.Approved, DateTimeOffset.UtcNow), Json);

            using var req = new HttpRequestMessage(HttpMethod.Post,
                r.Mode == "original" ? "/demo/original/confirm-payment" : "/payments/confirm-payment")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            req.Headers.Add(PaymentsController.TimestampHeader, ts);
            req.Headers.Add("X-Demo-Latency", Math.Clamp(r.LatencyMs, 0, 200).ToString(CultureInfo.InvariantCulture));
            req.Headers.Add(PaymentsController.SignatureHeader, r.ValidSignature
                ? Convert.ToHexString(SignatureVerifier.Sign(options.Value.WebhookSecret, ts, body))
                : new string('0', 64));

            await Task.Yield();
            var sw = Stopwatch.StartNew();
            using var res = await client.SendAsync(req);
            return new SendResult(eventId, (int)res.StatusCode, await res.Content.ReadAsStringAsync(), sw.ElapsedMilliseconds);
        }).ToList();

        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// El fragmento original de la prueba, tal cual, salvo que guarda las ganancias en una
    /// tabla aparte (LegacyDriverEarnings, sin índice único) para no mezclarlas con la solución.
    /// No verifica firma, importe ni estado: igual que el original.
    /// </summary>
    [HttpPost("original/confirm-payment")]
    public async Task<IActionResult> OriginalConfirmPayment(PaymentNotification request)
    {
        var order = await db.Orders.FindAsync(request.OrderId);

        if (order == null)
            return NotFound();

        // Demo: latencia de una base remota entre leer y guardar (interruptor visible en la UI).
        // No cambia la lógica; solo hace reproducible la carrera que en producción ocurre igual.
        if (int.TryParse(Request.Headers["X-Demo-Latency"], out var latency) && latency > 0)
            await Task.Delay(latency);

        if (order.Status == "PAID")
            return Ok(new { result = "Ok (ya estaba PAID)" });

        order.Status = "PAID";

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO LegacyDriverEarnings (OrderId, DriverId, Amount) VALUES ({order.Id}, {order.DriverId}, {order.DeliveryFee * 0.70m})");
            await tx.CommitAsync();
        }

        await restaurant.Receive(order.Id, null, "original");   // si el restaurante está caído: excepción → 500

        return Ok(new { result = "Ok (marcado PAID)" });
    }

    [HttpGet("state")]
    public async Task<object> State()
    {
        var legacy = await db.Database
            .SqlQuery<LegacyEarningRow>($"SELECT TOP 300 Id, OrderId, DriverId, Amount FROM LegacyDriverEarnings ORDER BY Id DESC")
            .ToListAsync();

        return new
        {
            restaurantDown = restaurant.Down,
            orders = await db.Orders.AsNoTracking().OrderByDescending(o => o.Id).Take(200).ToListAsync(),
            earnings = await db.DriverEarnings.AsNoTracking().OrderByDescending(e => e.Id).Take(200).ToListAsync(),
            legacyEarnings = legacy,
            notifications = await db.PaymentNotifications.AsNoTracking().OrderByDescending(n => n.Id).Take(200).ToListAsync(),
            outbox = await db.OutboxMessages.AsNoTracking().OrderByDescending(m => m.Id).Take(200).ToListAsync(),
            restaurantReceived = restaurant.Received,
            now = DateTime.UtcNow,
        };
    }

    public sealed record LegacyEarningRow(long Id, int OrderId, int? DriverId, decimal Amount);

    public sealed record RestaurantState(bool Down);

    [HttpPost("restaurant")]
    public IActionResult SetRestaurant(RestaurantState s)
    {
        restaurant.Down = s.Down;
        return Ok();
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.PaymentNotifications.ExecuteDeleteAsync();
        await db.DriverEarnings.ExecuteDeleteAsync();
        await db.Database.ExecuteSqlAsync($"DELETE FROM LegacyDriverEarnings");
        await db.Orders.ExecuteDeleteAsync();
        restaurant.Clear();
        restaurant.Down = false;
        return Ok();
    }

    /// <summary>Crea la base y la tabla de la demo del código original.</summary>
    public static async Task PrepareDatabaseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<FudisDbContext>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ctx.Database.EnsureCreatedAsync();
                break;
            }
            catch (Exception) when (attempt < 30)
            {
                await Task.Delay(2000); // SQL Server aún arrancando
            }
        }
        await ctx.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('dbo.LegacyDriverEarnings') IS NULL
            CREATE TABLE dbo.LegacyDriverEarnings (
                Id bigint IDENTITY PRIMARY KEY,
                OrderId int NOT NULL,
                DriverId int NULL,
                Amount decimal(18,4) NOT NULL)
            """);
    }
}
