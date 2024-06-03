using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind.Domain;

public record Shipper([property: Key] int ShipperId, string CompanyName, string Phone);
