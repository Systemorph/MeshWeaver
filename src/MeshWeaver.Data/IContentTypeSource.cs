namespace MeshWeaver.Data;

/// <summary>
/// A data source that declares the CLR type of its node's CONTENT — the type a node-addressed
/// <see cref="SchemaReference"/> with no type name describes (<c>@{node}/schema/</c>).
/// <para>The content of a mesh node rides inside <c>MeshNode.Content</c> and has no type source of its
/// own, so without this declaration the default schema fell to "the first registered type that is not
/// <c>MeshNode</c>" — whatever satellite type happened to register first (a node's schema came back as
/// <c>Comment</c>), or <c>MeshNode</c> itself.</para>
/// </summary>
public interface IContentTypeSource
{
    /// <summary>The content type, or null when the source declares none.</summary>
    Type? ContentType { get; }
}
