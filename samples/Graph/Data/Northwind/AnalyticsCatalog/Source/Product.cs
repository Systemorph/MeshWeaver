// <meshweaver>
// Id: Product
// DisplayName: Product
// </meshweaver>

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Product master data record.
/// </summary>
public record Product : INamed
{
    [Key]
    public int ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    [DisplayName("Supplier")]
    [Dimension(typeof(Supplier))]
    public int SupplierId { get; init; }

    [DisplayName("Category")]
    [Dimension(typeof(Category))]
    public int CategoryId { get; init; }

    public string QuantityPerUnit { get; init; } = string.Empty;

    public double UnitPrice { get; init; }

    public short UnitsInStock { get; init; }

    public short UnitsOnOrder { get; init; }

    public short ReorderLevel { get; init; }

    public string Discontinued { get; init; } = "0";

    string INamed.DisplayName => ProductName;
}
