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
    /// <summary>
    /// A configuration whose type declared nothing has no declaration — whatever the node type is
    /// called. The two names here are exactly the ones the deleted fallback answered for.
    /// </summary>
    [Theory]
    [InlineData("Deck")]
    [InlineData("Publish/Deck")]
    [InlineData("Markdown")]
    [InlineData(null)]
    public void UndeclaredType_HasNoExports_WhateverItIsCalled(string? nodeType)
    {
        // The node type's name is deliberately unused by the read: it is not an input to the
        // question any more, which is the whole point. It stays a parameter so the case names
        // record WHICH names a fallback used to answer for.
        Assert.NotNull(nodeType ?? "(null)");

        var configuration = new MessageHubConfiguration(null, new Address("X"));

        Assert.Null(configuration.Get<ExportDeclaration>());
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
