
namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Represents a summary item for an order within the Northwind application, encapsulating key information about an order such as the customer, number of products, and purchase date.
/// </summary>
/// <param name="Customer">
/// The identifier or name of the customer who placed the order.
/// </param>
/// <param name="Amount">
/// The total value of products purchased in the order.
/// </param>
/// <param name="Purchased">
/// The date when the order was purchased.
/// </param>
public record OrderSummaryItem(string Customer, double Amount, DateTime Purchased);
