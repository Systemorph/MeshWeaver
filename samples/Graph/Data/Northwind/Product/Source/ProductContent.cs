// <meshweaver>
// Id: ProductContent
// DisplayName: Product Content
// </meshweaver>

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Product content type for MeshNode instances.
/// </summary>
public record ProductContent
{
    [Key]
    public int ProductId { get; init; }

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string ProductName { get; init; } = string.Empty;

    [DisplayName("Supplier")]
    [Dimension(typeof(Supplier))]
    public int SupplierId { get; init; }

    [DisplayName("Category")]
    [Dimension(typeof(Category))]
    public int CategoryId { get; init; }

    public string QuantityPerUnit { get; init; } = string.Empty;

    [DisplayFormat(DataFormatString = "{0:C}")]
    public double UnitPrice { get; init; }

    public short UnitsInStock { get; init; }

    public short UnitsOnOrder { get; init; }

    public short ReorderLevel { get; init; }

    public bool Discontinued { get; init; }
}
