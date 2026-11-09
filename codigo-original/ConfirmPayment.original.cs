// Fragmento original entregado en la prueba. Se conserva solo como referencia;
// no se compila. La propuesta está en src/Fudis.Payments.

[HttpPost("confirm-payment")]
public async Task<IActionResult> ConfirmPayment(
    PaymentNotification request)
{
    var order = await _db.Orders
        .FindAsync(request.OrderId);

    if (order == null)
        return NotFound();

    if (order.Status == "PAID")
        return Ok();

    order.Status = "PAID";

    _db.DriverEarnings.Add(new DriverEarning
    {
        OrderId = order.Id,
        DriverId = order.DriverId,
        Amount = order.DeliveryFee * 0.70m
    });

    await _db.SaveChangesAsync();

    await _notificationService
        .NotifyRestaurant(order.Id);

    return Ok();
}
