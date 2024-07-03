using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

public record Category(
    [property: Key] int CategoryId,
    string CategoryName,
    string Description,
    string Picture
) : INamed
{
    string INamed.DisplayName => CategoryName;
}
