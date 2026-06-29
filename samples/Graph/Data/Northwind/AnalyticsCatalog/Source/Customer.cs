// <meshweaver>
// Id: Customer
// DisplayName: Customer
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Customer master data record.
/// </summary>
public record Customer : INamed
{
    [Key]
    public string CustomerId { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string ContactName { get; init; } = string.Empty;

    public string ContactTitle { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string PostalCode { get; init; } = string.Empty;

    public string Country { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string Fax { get; init; } = string.Empty;

    string INamed.DisplayName => CompanyName;
}
