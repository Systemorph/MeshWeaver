// <meshweaver>
// Id: LineOfBusiness
// DisplayName: Line of Business Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;


/// <summary>
/// Line of business dimension for insurance classification.
/// </summary>
public record LineOfBusiness : INamed
{
    /// <summary>
    /// Line of business code.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the line of business.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of the line of business.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order in lists.
    /// </summary>
    public int Order { get; init; }

    string INamed.DisplayName => Name;

    public static readonly LineOfBusiness Property = new()
    {
        Id = "PROP",
        Name = "Property",
        Description = "Property insurance covering buildings, contents, and business interruption",
        Order = 0
    };

    public static readonly LineOfBusiness Casualty = new()
    {
        Id = "CAS",
        Name = "Casualty",
        Description = "Casualty insurance covering liability and workers compensation",
        Order = 1
    };

    public static readonly LineOfBusiness Marine = new()
    {
        Id = "MARINE",
        Name = "Marine",
        Description = "Marine and cargo insurance",
        Order = 2
    };

    public static readonly LineOfBusiness Aviation = new()
    {
        Id = "AVIATION",
        Name = "Aviation",
        Description = "Aviation and aerospace insurance",
        Order = 3
    };

    public static readonly LineOfBusiness Energy = new()
    {
        Id = "ENERGY",
        Name = "Energy",
        Description = "Energy sector insurance including oil & gas",
        Order = 4
    };

    public static readonly LineOfBusiness[] All = [Property, Casualty, Marine, Aviation, Energy];

    public static LineOfBusiness GetById(string? id) =>
        All.FirstOrDefault(l => l.Id == id) ?? Property;
}
