using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Shipper details.
/// </summary>
/// <param name="ShipperId"></param>
/// <param name="CompanyName"></param>
/// <param name="Phone"></param>
[Icon(OpenSmcIcons.Provider, "sm-archive")]
public record Shipper([property: Key] int ShipperId, string CompanyName, string Phone) : INamed
{
    string INamed.DisplayName => CompanyName;
}
