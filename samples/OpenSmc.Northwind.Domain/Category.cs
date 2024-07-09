using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Category of products.
/// </summary>
/// <param name="CategoryId">Primary key.</param>
/// <param name="CategoryName"></param>
/// <param name="Description"></param>
/// <param name="Picture"></param>
[Icon(FluentIcons.Provider, "Album")]
public record Category(
    [property: Key] int CategoryId,
    string CategoryName,
    string Description,
    string Picture
) : INamed
{
    string INamed.DisplayName => CategoryName;
}
