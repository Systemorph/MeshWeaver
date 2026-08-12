namespace MeshWeaver.Markdown.Export.Ast;

/// <summary>
/// One chapter of an export: a title, its markdown, and — crucially — the mesh path of the node
/// the markdown came FROM.
///
/// <para>The node path is not decoration. A relative embed (<c>@@("area:Foo")</c>) means "the Foo
/// area of the node this text lives in", so it can only be resolved against that node's own path.
/// When <c>IncludeChildren</c> is on, an export is the root document plus one chapter per
/// descendant — each a different node — so a single path for the whole document resolves every
/// descendant's relative embeds against the WRONG address, and two descendants that both write
/// <c>@@("area:Foo")</c> would collide into one cache entry and render each other's content.</para>
///
/// <para>Carrying the path per chapter removes both failures by construction.</para>
/// </summary>
/// <param name="Title">Chapter heading (the node's name).</param>
/// <param name="Markdown">The chapter's markdown body.</param>
/// <param name="NodePath">
/// The owning node's mesh path. Null is allowed (a synthetic chapter with no node behind it);
/// relative embeds in such a chapter simply do not resolve.
/// </param>
public record ExportChapter(string Title, string Markdown, string? NodePath = null);
