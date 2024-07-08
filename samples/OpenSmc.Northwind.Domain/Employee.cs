using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Employee details.
/// </summary>
/// <param name="EmployeeId"></param>
/// <param name="LastName"></param>
/// <param name="FirstName"></param>
/// <param name="Title"></param>
/// <param name="TitleOfCourtesy"></param>
/// <param name="BirthDate"></param>
/// <param name="HireDate"></param>
/// <param name="Address"></param>
/// <param name="City"></param>
/// <param name="Region"></param>
/// <param name="PostalCode"></param>
/// <param name="Country"></param>
/// <param name="HomePhone"></param>
/// <param name="Extension"></param>
/// <param name="Photo"></param>
/// <param name="Notes"></param>
/// <param name="ReportsTo"></param>
/// <param name="PhotoPath"></param>
[Icon(OpenSmcIcons.Provider, "sm-archive")]

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
