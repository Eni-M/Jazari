using System.Collections.Generic;
using System.Linq;

namespace Shop;

public interface IStore
{
    void Save(Order order);
    Order? Find(string customer);
}

public class MemoryStore : IStore
{
    private readonly List<Order> _orders = new();

    public void Save(Order order) => _orders.Add(order);

    public Order? Find(string customer) => _orders.FirstOrDefault(o => o.Customer == customer);
}
