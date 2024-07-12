using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Product details.
/// </summary>
/// <param name="ProductId">Primary key</param>
/// <param name="ProductName"></param>
/// <param name="SupplierId">Id of the <see cref="Supplier"/></param>
/// <param name="CategoryId">Id of the <see cref="Category"/></param>
/// <param name="QuantityPerUnit"></param>
/// <param name="UnitPrice"></param>
/// <param name="UnitsInStock"></param>
/// <param name="UnitsOnOrder"></param>
/// <param name="ReorderLevel"></param>
/// <param name="Discontinued"></param>
[Icon(FluentIcons.Provider, "Album")]
public record Product(
    [property: Key] int ProductId,
    string ProductName,
    int SupplierId,
    int CategoryId,
    string QuantityPerUnit,
    decimal UnitPrice,
    short UnitsInStock,
    short UnitsOnOrder,
    short ReorderLevel,
    string Discontinued
) : INamed
{
    string INamed.DisplayName => ProductName;
}
