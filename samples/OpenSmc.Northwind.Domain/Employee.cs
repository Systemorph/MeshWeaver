using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

public record Employee(
    [property: Key] int EmployeeId,
    string LastName,
    string FirstName,
    string Title,
    string TitleOfCourtesy,
    DateTime BirthDate,
    DateTime HireDate,
    string Address,
    string City,
    string Region,
    string PostalCode,
    string Country,
    string HomePhone,
    string Extension,
    string Photo,
    string Notes,
    int ReportsTo,
    string PhotoPath
) : INamed
{
    string INamed.DisplayName => $"{FirstName} {LastName}";
}
