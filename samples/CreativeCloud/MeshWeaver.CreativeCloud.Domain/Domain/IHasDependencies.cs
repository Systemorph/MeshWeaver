namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Interface for entities that have dependencies on other entities.
/// Dependencies are expressed as fully qualified names in the format "{type}:{id}".
/// </summary>
public interface IHasDependencies
{
    /// <summary>
    /// Collection of fully qualified dependency names in the format "{type}:{id}".
    /// Examples: "person:john-doe", "story:my-story", "arch:main-arch"
    /// </summary>
    IReadOnlyCollection<string> Dependencies { get; }
}
