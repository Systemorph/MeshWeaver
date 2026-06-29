namespace MeshWeaver.NuGet;

/// <summary>
/// A requested NuGet package: its id and an optional version constraint.
/// </summary>
/// <param name="Id">The NuGet package id.</param>
/// <param name="VersionRange">The version or version range to resolve, or <c>null</c> to pick the highest available version.</param>
public sealed record NuGetPackageReference(string Id, string? VersionRange);
