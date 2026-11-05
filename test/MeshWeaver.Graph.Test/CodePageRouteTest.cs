using System;
using MeshWeaver.Data;
using MeshWeaver.AI;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The NodeType page's <c>Code</c> link must ROUND-TRIP to the file it names (measured on a client
/// instance, 2026-11-05: <c>Acme/Rating/Quote/Code/Acme%2FRating%2FQuote%2FTest%2FQuoteTests</c>
/// answered "not found"). The side menu writes the Code node path in
/// <see cref="WorkspaceReference.Encode"/> form — one URL segment — and every reader must decode it
/// exactly once: the area itself (<see cref="NodeTypeLayoutAreas.CodeNodePathOf"/>, which a shell
/// that splits the remainder on its own hands the RAW segment) and the MCP <c>{node}/area/{Name}/{id}</c>
/// route (<c>MeshOperations.TryParseAreaRoute</c>), which handed the area the still-encoded id —
/// the address the page then embedded named no node.
/// </summary>
public class CodePageRouteTest
{
    private const string TypePath = "Acme/Rating/Quote";
    private const string CodePath = "Acme/Rating/Quote/Test/QuoteTests";

    /// <summary>The href the NodeType side menu builds for a source file.</summary>
    private static string SideMenuHref()
        => new LayoutAreaReference(NodeTypeLayoutAreas.CodeArea) { Id = CodePath }.ToHref(new Address(TypePath));

    [Fact]
    public void TheSideMenuLink_IsTheTypeThenCodeThenOneEncodedSegment()
        => Assert.Equal("Acme/Rating/Quote/Code/Acme%2FRating%2FQuote%2FTest%2FQuoteTests", SideMenuHref());

    /// <summary>A portal page decodes the id once at its URL entry; the area keeps that path.</summary>
    [Fact]
    public void AnIdDecodedAtTheUrlEntry_NamesTheCodeNode()
    {
        var segment = SideMenuHref()[(TypePath + "/Code/").Length..];
        var decodedOnce = (string)WorkspaceReference.Decode(segment);

        Assert.Equal(CodePath, NodeTypeLayoutAreas.CodeNodePathOf(
            new LayoutAreaReference(NodeTypeLayoutAreas.CodeArea) { Id = decodedOnce }));
    }

    /// <summary>
    /// RED before the fix: a shell that splits the remainder itself passes the raw segment, and the
    /// area embedded <c>Acme%2FRating%2F…</c> as an ADDRESS.
    /// </summary>
    [Fact]
    public void AnIdLeftEncodedByTheShell_StillNamesTheCodeNode()
    {
        var raw = SideMenuHref()[(TypePath + "/Code/").Length..];

        Assert.Equal(CodePath, NodeTypeLayoutAreas.CodeNodePathOf(
            new LayoutAreaReference(NodeTypeLayoutAreas.CodeArea) { Id = raw }));
    }

    [Fact]
    public void NoId_NamesNoFile()
        => Assert.Null(NodeTypeLayoutAreas.CodeNodePathOf(new LayoutAreaReference(NodeTypeLayoutAreas.CodeArea)));

    /// <summary>
    /// RED before the fix: the MCP area route (<c>get {node}/area/Code/{id}</c>) handed the id on
    /// still encoded — the measured <c>Code/2</c> address <c>Claims%2FClaim%2FTest%2FClaimTests</c>.
    /// </summary>
    [Fact]
    public void TheMcpAreaRoute_DecodesTheIdOnce()
    {
        var ok = MeshOperations.TryParseAreaRoute(
            $"{TypePath}/area/{SideMenuHref()[(TypePath + "/").Length..]}",
            out var nodePath, out var area, out var id);

        Assert.True(ok);
        Assert.Equal(TypePath, nodePath);
        Assert.Equal(NodeTypeLayoutAreas.CodeArea, area);
        Assert.Equal(CodePath, id);
    }

    /// <summary>An id written with raw slashes has nothing to decode and stays the path it names.</summary>
    [Fact]
    public void TheMcpAreaRoute_KeepsARawSlashedId()
    {
        Assert.True(MeshOperations.TryParseAreaRoute(
            $"{TypePath}/area/Code/{CodePath}", out _, out _, out var id));
        Assert.Equal(CodePath, id);
    }

    /// <summary>
    /// The sign-in action starts the platform's per-user GitHub connect flow and RETURNS to this
    /// Code page: its <c>returnPath</c> is exactly the side menu's link, so the reader lands back on
    /// the file they asked for.
    /// </summary>
    [Fact]
    public void TheGitHubSignIn_ReturnsToTheSameCodePage()
    {
        var href = NodeTypeLayoutAreas.GitHubSignInHref(new Address(TypePath), CodePath);

        Assert.StartsWith("/connect/github?returnPath=", href);
        var returnPath = Uri.UnescapeDataString(href["/connect/github?returnPath=".Length..]);
        Assert.Equal("/" + SideMenuHref(), returnPath);
    }

    [Theory]
    [InlineData("Acme/Type/Source/A.cs", "csharp")]
    [InlineData("Acme/Type.json", "json")]
    [InlineData("Acme/Doc.md", "markdown")]
    [InlineData("Acme/README", "plaintext")]
    public void TheEditorLanguage_FollowsTheExtension(string path, string language)
        => Assert.Equal(language, NodeTypeLayoutAreas.LanguageOf(path));

    [Fact]
    public void TheOrigin_LinksToTheFileAtItsRef()
    {
        var origin = new CodeSourceOrigin(
            "https://github.com/acme/modules", "c1fe2dccd61e855bc5af1d60d8a494f965d6bd80", "Acme/Type/Test/T.cs");

        Assert.Equal(
            "https://github.com/acme/modules/blob/c1fe2dccd61e855bc5af1d60d8a494f965d6bd80/Acme/Type/Test/T.cs",
            origin.BrowseUrl);
        Assert.Equal("c1fe2dc", origin.RefLabel);
        Assert.Equal("main", (origin with { Ref = "main" }).RefLabel);
    }
}
