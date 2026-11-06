using System.Threading.Tasks;

namespace Shop;

public class Billing
{
    private readonly IStore _store;

    public Billing(IStore store) { _store = store; }

    public string Name { get; } = "";

    public decimal Charge(string customer)
    {
        var order = _store.Find(customer);
        if (order is null) return 0;
        return order.Total();
    }

    public int WholeTotal(Order order) { return (int)order.Total(); }

    public string Label(Order order) { return Format(order.Customer, order.Count()); }

    private static string Format(string name, int count) => $"{name}: {count}";

    public async Task NotifyAsync() { await SendAsync(); }

    private static Task SendAsync() => Task.CompletedTask;
}
