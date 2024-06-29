using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

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
    bool Discontinued
) : INamed
{
    string INamed.DisplayName => ProductName;
}
