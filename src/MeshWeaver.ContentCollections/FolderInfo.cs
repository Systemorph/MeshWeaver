#nullable enable
namespace MeshWeaver.ContentCollections;

// Base class for collection items
/// <summary>Base type for an item listed within a content collection (a folder or a file).</summary>
/// <param name="Path">The item's path within the collection.</param>
/// <param name="Name">The item's display name (last path segment).</param>
public abstract record CollectionItem(string Path, string Name);

// Folder item
/// <summary>A folder entry within a content collection.</summary>
/// <param name="Path">The folder's path within the collection.</param>
/// <param name="Name">The folder's display name.</param>
/// <param name="ItemCount">The number of immediate child entries in the folder.</param>
public record FolderItem(string Path, string Name, int ItemCount) : CollectionItem(Path, Name);

// File item
/// <summary>A file entry within a content collection.</summary>
/// <param name="Path">The file's path within the collection.</param>
/// <param name="Name">The file's display name.</param>
/// <param name="LastModified">The file's last-modified timestamp.</param>
public record FileItem(string Path, string Name, DateTime LastModified) : CollectionItem(Path, Name);
