using System;
using System.Linq;
using System.Reflection;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The export contract after #1576: a node type's export behaviour is what it DECLARES on its own
/// hub configuration, and nothing else. No compiled reader may recover a declaration from what a
/// type is CALLED.
///
/// <para><b>Why this is pinned rather than assumed.</b> <see cref="ExportDeclaration"/> shipped
/// with a transition fallback — <c>Resolve(configuration, nodeType)</c>, which returned
/// <see cref="ExportDeclaration.SlideDeck"/> for any type whose name ended in <c>/Deck</c> — so
/// plugin decks compiled against a pre-<c>WithExport</c> platform kept their Export menu. That
/// fallback is gone (<c>Publish/Deck</c> declares: MeshWeaver.Plugins#553), and re-introducing one
/// would take the whole subsystem back to guessing from names: the failure mode is not an error
/// but an EMPTY document from a green export, because a name gate silently excludes every plugin
/// type it was not written to recognise.</para>
/// </summary>
public class ExportDeclarationTest
{
    /// <summary>A configuration whose type declared nothing has no declaration.</summary>
    [Fact]
    public void UndeclaredType_HasNoExports()
    {
        var configuration = new MessageHubConfiguration(null, new Address("X"));

        Assert.Null(configuration.Get<ExportDeclaration>());
    }

    /// <summary>
    /// 🚨 <b>No public export API may take a node type at all</b> — the guard that actually bites.
    ///
    /// <para>A test that merely reads an undeclared configuration cannot tell the two designs
    /// apart: <c>Get&lt;ExportDeclaration&gt;()</c> returns null either way, so it would pass
    /// unchanged if <c>Resolve(configuration, nodeType)</c> came back. What distinguishes them is
    /// the SHAPE OF THE SURFACE — the deleted fallback existed because a reader could ask "and what
    /// is this type called?", and the fix was to stop offering anywhere to ask. This asserts that:
    /// no public member of the export declaration surface accepts a node-type parameter, so
    /// reintroducing a name-based resolver fails here rather than silently restoring the guessing.
    /// (Copilot review of this PR, which correctly called the first version tautological.)</para>
    /// </summary>
    [Fact]
    public void NoPublicExportApi_AcceptsANodeType()
    {
        var offenders = new[] { typeof(ExportDeclaration), typeof(ExportDeclarationExtensions) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetParameters().Any(p =>
                p.Name is not null
                && p.Name.Contains("nodeType", StringComparison.OrdinalIgnoreCase)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.Name))})")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "The export declaration is resolved from the type's hub CONFIGURATION alone. A member "
            + "taking a node type is how the deleted suffix fallback was expressed, and its failure "
            + "mode is silent — an unrecognised type composes as a plain document and a deck-shaped "
            + "node exports an EMPTY file from a green activity. Found: "
            + string.Join("; ", offenders));
    }

    /// <summary>A type that declares gets exactly what it declared — the only route in.</summary>
    [Fact]
    public void DeclaredType_GetsExactlyItsDeclaration()
    {
        var configuration = new MessageHubConfiguration(null, new Address("X"))
            .WithExport(ExportDeclaration.SlideDeck);

        var declaration = configuration.Get<ExportDeclaration>();

        Assert.NotNull(declaration);
        Assert.Equal(ExportComposition.SlideDeck, declaration!.Composition);
        Assert.True(declaration.Formats.HasFlag(ExportFormats.Pdf));
        Assert.True(declaration.Formats.HasFlag(ExportFormats.Send));
        Assert.False(declaration.Formats.HasFlag(ExportFormats.Docx),
            "a deck carries no markdown body of its own, so DOCX would render an empty document");
    }

    /// <summary>
    /// The built-in Markdown type declares Document composition — the platform's own type goes
    /// through the same door as every plugin type, with no shortcut for being built in.
    /// </summary>
    [Fact]
    public void BuiltInMarkdownType_DeclaresDocumentComposition()
    {
        var node = MarkdownNodeType.CreateMeshNode();
        Assert.NotNull(node.HubConfiguration);

        var configuration = node.HubConfiguration!(
            new MessageHubConfiguration(null, new Address(MarkdownNodeType.NodeType)));

        var declaration = configuration.Get<ExportDeclaration>();
        Assert.NotNull(declaration);
        Assert.Equal(ExportComposition.Document, declaration!.Composition);
    }
}
