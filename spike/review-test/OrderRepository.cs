using Microsoft.Data.SqlClient;

namespace Shop;

public sealed class OrderRepository(string connectionString)
{
    public List<Order> FindByCustomer(string customerName)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT Id, Customer, Total FROM Orders WHERE Customer = '" + customerName + "'", connection);
        using var reader = command.ExecuteReader();
        var orders = new List<Order>();
        while (reader.Read()) orders.Add(new Order(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2)));
        return orders;
    }
}
