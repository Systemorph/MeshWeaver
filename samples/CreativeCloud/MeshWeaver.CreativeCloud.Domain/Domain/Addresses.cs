using MeshWeaver.Messaging;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Address for a Person entity.
/// </summary>
public record PersonAddress(string Id) : Address(TypeName, Id)
{
    /// <summary>The type name for person addresses.</summary>
    public const string TypeName = "person";
}

/// <summary>
/// Address for a StoryArch entity.
/// </summary>
public record ArchAddress(string Id) : Address(TypeName, Id)
{
    /// <summary>The type name for arch addresses.</summary>
    public const string TypeName = "arch";
}

/// <summary>
/// Address for a Story entity.
/// </summary>
public record StoryAddress(string Id) : Address(TypeName, Id)
{
    /// <summary>The type name for story addresses.</summary>
    public const string TypeName = "story";
}

/// <summary>
/// Address for a Video entity.
/// </summary>
public record VideoAddress(string Id) : Address(TypeName, Id)
{
    /// <summary>The type name for video addresses.</summary>
    public const string TypeName = "video";
}

/// <summary>
/// Address for an Event entity.
/// </summary>
public record EventAddress(string Id) : Address(TypeName, Id)
{
    /// <summary>The type name for event addresses.</summary>
    public const string TypeName = "event";
}

/// <summary>
/// Address for a Post entity.
/// </summary>
public record PostAddress(string Id) : Address(TypeName, Id)
{
    /// <summary>The type name for post addresses.</summary>
    public const string TypeName = "post";
}
