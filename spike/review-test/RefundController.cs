using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Shop;

[ApiController, Route("api/refunds"), Authorize]
public sealed class RefundController(IOrderStore orders, IPayments payments) : ControllerBase
{
    [HttpPost("{orderId:int}")]
    public async Task<IActionResult> Refund(int orderId)
    {
        var order = await orders.GetAsync(orderId);
        if (order is null) return NotFound();
        await payments.RefundAsync(order.Id, order.Total);
        return Ok();
    }
}
