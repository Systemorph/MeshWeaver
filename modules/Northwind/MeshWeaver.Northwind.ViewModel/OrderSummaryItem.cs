
namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Represents a summary item for an order within the Northwind application, encapsulating key information about an order such as the customer, number of products, and purchase date.
/// </summary>
/// <param name="Customer">
/// The identifier or name of the customer who placed the order.
/// </param>
/// <param name="Products">
/// The total number of products included in the order.
/// </param>
/// <param name="Purchased">
/// The date when the order was purchased.
/// </param>
public record OrderSummaryItem(string Customer, int Products, DateTime Purchased);
