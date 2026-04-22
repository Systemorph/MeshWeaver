// <meshweaver>
// Id: Order
// DisplayName: Order
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Order entity loaded from CSV.
/// </summary>
public record Order
{
    [Key]
    public int OrderId { get; init; }

    [Dimension(typeof(Customer))]
    public string CustomerId { get; init; } = string.Empty;

    [Dimension(typeof(Employee))]
    public int EmployeeId { get; init; }

    public DateTime OrderDate { get; init; }

    public DateTime RequiredDate { get; init; }

    public DateTime ShippedDate { get; init; }

    public int ShipVia { get; init; }

    public decimal Freight { get; init; }

    public string ShipName { get; init; } = string.Empty;

    public string ShipCity { get; init; } = string.Empty;

    public string ShipRegion { get; init; } = string.Empty;

    public string ShipPostalCode { get; init; } = string.Empty;

    public string ShipCountry { get; init; } = string.Empty;
}
