namespace MeshWeaver.Layout;

/// <summary>
/// Marks a string property as a content item reference (e.g., an image/file URL
/// from a content collection). On the standard Edit page, properties with this
/// attribute render a text field with a "Browse" button that opens a modal
/// FileBrowser dialog for selecting files from the node's content collection.
/// </summary>
/// <param name="collection">
/// The content collection to browse. Defaults to "content".
/// </param>
[AttributeUsage(AttributeTargets.Property)]
public class ContentItemAttribute(string collection = "content") : Attribute
{
    /// <summary>
    /// The content collection name to browse.
    /// </summary>
    public string Collection { get; } = collection;
}
