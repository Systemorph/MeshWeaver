// <meshweaver>
// Id: PrimeReport
// DisplayName: Prime Report Data Model
// </meshweaver>

using MeshWeaver.Domain;

public record PrimeReport
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>How many primes the Python script computes (clamped to 1..200 at render time).</summary>
    public int Count { get; init; } = 25;
}
