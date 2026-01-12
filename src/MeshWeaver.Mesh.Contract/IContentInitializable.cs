namespace MeshWeaver.Mesh;

/// <summary>
/// Interface for content types that need initialization after deserialization.
/// Used by ContentTypeSource to allow content to transform itself after loading.
/// Common use case: calculating dates from offsets for test data.
/// </summary>
public interface IContentInitializable
{
    /// <summary>
    /// Called after content is loaded from persistence.
    /// Returns a potentially modified instance.
    /// </summary>
    object Initialize();
}
