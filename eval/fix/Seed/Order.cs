using System.Collections.Generic;
using System.Linq;

namespace Shop;

public class Order
{
    public string Customer { get; set; } = "";
    public List<decimal> Prices { get; } = new();

    public decimal Total() => Prices.Sum();

    public int Count() { return Prices.Count; }

    public decimal Discount(decimal percent)
    {
        var total = Total();
        return total * percent / 100;
    }
}
