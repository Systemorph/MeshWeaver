// <meshweaver>
// Id: EmployeeContent
// DisplayName: Employee Content
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Employee content type for MeshNode instances.
/// </summary>
public record EmployeeContent
{
    [Key]
    public int EmployeeId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string FullName => $"{FirstName} {LastName}";

    public string Title { get; init; } = string.Empty;

    public string TitleOfCourtesy { get; init; } = string.Empty;

    public DateTime BirthDate { get; init; }

    public DateTime HireDate { get; init; }

    public string City { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    [Dimension(typeof(string), nameof(Country))]
    public string Country { get; init; } = string.Empty;

    public int ReportsTo { get; init; }
}
