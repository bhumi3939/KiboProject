namespace CommerceHub.API.Configuration;

public class MongoDbSettings
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "CommerceHub";
    public string OrdersCollection { get; set; } = "Orders";
    public string ProductsCollection { get; set; } = "Products";
}

public class RabbitMqSettings
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string ExchangeName { get; set; } = "commerce.events";
    public string OrderCreatedQueue { get; set; } = "order.created";
    public string OrderCreatedRoutingKey { get; set; } = "order.created";
}
