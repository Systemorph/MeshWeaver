using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

public record Shipper([property: Key] int ShipperId, string CompanyName, string Phone) : INamed
{
    string INamed.DisplayName => CompanyName;
}
