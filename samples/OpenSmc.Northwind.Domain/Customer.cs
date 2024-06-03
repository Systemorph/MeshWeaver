using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind.Domain;

public record Customer(
    [property: Key] string CustomerId,
    string CompanyName,
    string ContactName,
    string ContactTitle,
    string Address,
    string City,
    string Region,
    string PostalCode,
    string Country,
    string Phone,
    string Fax
);
