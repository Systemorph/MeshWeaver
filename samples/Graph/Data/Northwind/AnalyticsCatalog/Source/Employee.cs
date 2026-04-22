// <meshweaver>
// Id: Employee
// DisplayName: Employee
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Employee master data record.
/// </summary>
public record Employee : INamed
{
    [Key]
    public int EmployeeId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string TitleOfCourtesy { get; init; } = string.Empty;

    public DateTime BirthDate { get; init; }

    public DateTime HireDate { get; init; }

    public string City { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string Country { get; init; } = string.Empty;

    public int ReportsTo { get; init; }

    string INamed.DisplayName => $"{FirstName} {LastName}";
}
