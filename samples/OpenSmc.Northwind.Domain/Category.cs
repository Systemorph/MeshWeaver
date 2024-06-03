using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind.Domain;

public record Category(
    [property: Key] int CategoryId,
    string CategoryName,
    string Description,
    string Picture
);
