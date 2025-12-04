using MeshWeaver.Messaging;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Helper class for working with fully qualified dependency names.
/// </summary>
public static class DependencyHelper
{
    /// <summary>
    /// Creates a fully qualified name from a type and id.
    /// </summary>
    /// <param name="type">The entity type (e.g., "person", "story", "arch").</param>
    /// <param name="id">The entity id.</param>
    /// <returns>A fully qualified name in the format "{type}:{id}".</returns>
    public static string ToFullyQualifiedName(string type, string id) => $"{type}:{id}";

    /// <summary>
    /// Parses a fully qualified name into its type and id components.
    /// </summary>
    /// <param name="fullyQualifiedName">The fully qualified name (e.g., "person:john-doe").</param>
    /// <returns>A tuple containing the type and id.</returns>
    /// <exception cref="ArgumentException">Thrown when the name format is invalid.</exception>
    public static (string Type, string Id) Parse(string fullyQualifiedName)
    {
        var colonIndex = fullyQualifiedName.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= fullyQualifiedName.Length - 1)
            throw new ArgumentException($"Invalid fully qualified name: {fullyQualifiedName}. Expected format: 'type:id'");

        return (fullyQualifiedName[..colonIndex], fullyQualifiedName[(colonIndex + 1)..]);
    }

    /// <summary>
    /// Tries to parse a fully qualified name into its type and id components.
    /// </summary>
    /// <param name="fullyQualifiedName">The fully qualified name.</param>
    /// <param name="type">The parsed type, or null if parsing failed.</param>
    /// <param name="id">The parsed id, or null if parsing failed.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse(string? fullyQualifiedName, out string? type, out string? id)
    {
        type = null;
        id = null;

        if (string.IsNullOrEmpty(fullyQualifiedName))
            return false;

        var colonIndex = fullyQualifiedName.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= fullyQualifiedName.Length - 1)
            return false;

        type = fullyQualifiedName[..colonIndex];
        id = fullyQualifiedName[(colonIndex + 1)..];
        return true;
    }

    /// <summary>
    /// Generates a details URL from a fully qualified name.
    /// </summary>
    /// <param name="fullyQualifiedName">The fully qualified name (e.g., "person:john-doe").</param>
    /// <param name="hubAddress">The hub address to prefix the URL.</param>
    /// <returns>A URL to the entity's Overview page.</returns>
    public static string ToDetailsUrl(string fullyQualifiedName, Address hubAddress)
    {
        var (type, id) = Parse(fullyQualifiedName);
        return $"/{hubAddress}/{type}/{id}/Overview";
    }

    /// <summary>
    /// Generates a details URL from a fully qualified name.
    /// </summary>
    /// <param name="fullyQualifiedName">The fully qualified name (e.g., "person:john-doe").</param>
    /// <param name="hubAddress">The hub address string to prefix the URL.</param>
    /// <returns>A URL to the entity's Overview page.</returns>
    public static string ToDetailsUrl(string fullyQualifiedName, string hubAddress)
    {
        var (type, id) = Parse(fullyQualifiedName);
        return $"/{hubAddress}/{type}/{id}/Overview";
    }

    /// <summary>
    /// Creates a fully qualified name for a Person.
    /// </summary>
    public static string Person(string id) => ToFullyQualifiedName(PersonAddress.TypeName, id);

    /// <summary>
    /// Creates a fully qualified name for a StoryArch.
    /// </summary>
    public static string Arch(string id) => ToFullyQualifiedName(ArchAddress.TypeName, id);

    /// <summary>
    /// Creates a fully qualified name for a Story.
    /// </summary>
    public static string Story(string id) => ToFullyQualifiedName(StoryAddress.TypeName, id);

    /// <summary>
    /// Creates a fully qualified name for a Video.
    /// </summary>
    public static string Video(string id) => ToFullyQualifiedName(VideoAddress.TypeName, id);

    /// <summary>
    /// Creates a fully qualified name for an Event.
    /// </summary>
    public static string Event(string id) => ToFullyQualifiedName(EventAddress.TypeName, id);

    /// <summary>
    /// Creates a fully qualified name for a Post.
    /// </summary>
    public static string Post(string id) => ToFullyQualifiedName(PostAddress.TypeName, id);
}
