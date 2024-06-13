using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

public record OrderDetails(
    int OrderId,
    [property: Dimension(typeof(Product))] int ProductId,
    double UnitPrice,
    int Quantity,
    double Discount
)
{
    /// <summary>
    /// Ids should be generated depending on data storage (e.g. auto-numbering long), string, Guid, etc. No semantic meaning can be given to the ID.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

}
