#nullable enable
namespace MeshWeaver.ContentCollections;

// Base class for collection items
public abstract record CollectionItem(string Path, string Name);

// Folder item
public record FolderItem(string Path, string Name, int ItemCount) : CollectionItem(Path, Name);

// File item
public record FileItem(string Path, string Name, DateTime LastModified) : CollectionItem(Path, Name);
