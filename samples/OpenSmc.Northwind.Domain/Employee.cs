using System;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain
{
    /// <summary>
    /// Represents an employee in the Northwind domain. This record encapsulates all relevant details about an employee, including personal information, job details, and contact information.
    /// </summary>
    /// <param name="EmployeeId">The unique identifier for the employee. This is marked as the primary key.</param>
    /// <param name="LastName">The last name of the employee.</param>
    /// <param name="FirstName">The first name of the employee.</param>
    /// <param name="Title">The job title of the employee.</param>
    /// <param name="TitleOfCourtesy">The title of courtesy for the employee (e.g., Mr., Ms., Dr.).</param>
    /// <param name="BirthDate">The birth date of the employee.</param>
    /// <param name="HireDate">The date the employee was hired.</param>
    /// <param name="Address">The physical address of the employee.</param>
    /// <param name="City">The city in which the employee lives.</param>
    /// <param name="Region">The region or state in which the employee lives.</param>
    /// <param name="PostalCode">The postal code for the employee's address.</param>
    /// <param name="Country">The country in which the employee resides.</param>
    /// <param name="HomePhone">The home phone number of the employee.</param>
    /// <param name="Extension">The phone extension number for the employee at work.</param>
    /// <param name="Photo">A binary representation of the employee's photo.</param>
    /// <param name="Notes">Notes about the employee.</param>
    /// <param name="ReportsTo">The identifier of the employee's supervisor.</param>
    /// <param name="PhotoPath">The path to the employee's photo.</param>
    /// <remarks>
    /// This record is decorated with an <see cref="IconAttribute"/> indicating the visual representation of an employee in UI components.
    /// </remarks>
    /// <seealso cref="IconAttribute"/>
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
}
