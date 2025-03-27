namespace MeshWeaver.Articles;

// Base class for collection items
public abstract record CollectionItem(string Name);

// Folder item
public record FolderItem(string Path, string Name, int ItemCount) : CollectionItem(Name);

// File item
public record FileItem(string Path, string Name, DateTime LastModified) : CollectionItem(Name);
