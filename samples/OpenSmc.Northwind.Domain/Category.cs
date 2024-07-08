using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Category of products.
/// </summary>
/// <param name="CategoryId"></param>
/// <param name="CategoryName"></param>
/// <param name="Description"></param>
/// <param name="Picture"></param>
[Icon(OpenSmcIcons.Provider, "sm-archive")]
public record Category(
    [property: Key] int CategoryId,
    string CategoryName,
    string Description,
    string Picture
) : INamed
{
    string INamed.DisplayName => CategoryName;
}
