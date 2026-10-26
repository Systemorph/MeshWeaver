using System.Xml.Linq;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The node-icon catalogue is the FIXED list an automatic icon assignment chooses from, and its
/// whole promise is that every name renders on every surface. These pin that promise: each entry
/// ships a tiled glyph the HTML surfaces can draw, each name is a well-formed Fluent name that
/// <see cref="MeshNodeImageHelper.IsFluentIconName"/> accepts, and no two entries collide.
/// </summary>
public class NodeIconCatalogTest
{
    private const string IconResourcePrefix = "MeshWeaver.Graph.Icons.";

    [Fact]
    public void Catalogue_IsCurated_NotEmpty()
        => NodeIconCatalog.Entries.Should().HaveCountGreaterThanOrEqualTo(100);

    [Fact]
    public void EveryEntry_HasAShippedGlyph()
    {
        var missing = NodeIconCatalog.Entries
            .Where(e => MeshNodeImageHelper.ShippedIconFor(e.Name) is null)
            .Select(e => e.Name)
            .ToList();
        missing.Should().BeEmpty();
    }

    [Fact]
    public void EveryEntry_ShippedGlyphUrl_IsTheLowerCasedName()
    {
        var wrong = NodeIconCatalog.Entries
            .Where(e => MeshNodeImageHelper.ShippedIconFor(e.Name)
                        != $"/static/NodeTypeIcons/{e.Name.ToLowerInvariant()}.svg")
            .Select(e => e.Name)
            .ToList();
        wrong.Should().BeEmpty();
    }

    [Fact]
    public void Names_AreUnique_CaseInsensitively()
    {
        var duplicates = NodeIconCatalog.Entries
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void Names_ArePascalCaseAscii_FluentNames()
    {
        var malformed = NodeIconCatalog.Entries
            .Where(e => !MeshNodeImageHelper.IsFluentIconName(e.Name))
            .Select(e => e.Name)
            .ToList();
        malformed.Should().BeEmpty();
    }

    [Fact]
    public void Meanings_AreShortKeywordHints()
    {
        var bad = NodeIconCatalog.Entries
            .Where(e => string.IsNullOrWhiteSpace(e.Meaning)
                        || e.Meaning.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Length is < 3 or > 8)
            .Select(e => e.Name)
            .ToList();
        bad.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("document")]
    [InlineData("DOCUMENTTEXT")]
    [InlineData(" Gavel ")]
    public void Contains_IsCaseInsensitive(string name)
        => NodeIconCatalog.Contains(name).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAFluentIconAtAll")]
    [InlineData("/static/NodeTypeIcons/document.svg")]
    [InlineData("OpenAI")]
    public void Contains_RejectsNonMembers(string? name)
        => NodeIconCatalog.Contains(name).Should().BeFalse();

    [Fact]
    public void EveryEntry_Glyph_IsATiledSquareSvg()
    {
        var assembly = typeof(MeshNodeImageHelper).Assembly;
        var problems = new List<string>();
        foreach (var entry in NodeIconCatalog.Entries)
        {
            var resource = IconResourcePrefix + entry.Name.ToLowerInvariant() + ".svg";
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                problems.Add($"{entry.Name}: no embedded resource {resource}");
                continue;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(stream);
            }
            catch (System.Xml.XmlException ex)
            {
                problems.Add($"{entry.Name}: not well-formed XML ({ex.Message})");
                continue;
            }

            var problem = TileProblem(doc, LegacyCanvasSize.GetValueOrDefault(entry.Name, "20"));
            if (problem is not null)
                problems.Add($"{entry.Name}: {problem}");
        }
        problems.Should().BeEmpty();
    }

    /// <summary>
    /// Glyphs that predate the catalogue and are reused as-is (the catalogue adds names, it does
    /// not redraw shipped files): <c>database.svg</c> was authored on a 24 px canvas. Every other
    /// entry, and every glyph added for the catalogue, is on the house 20 px tile.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyCanvasSize =
        new Dictionary<string, string> { ["Database"] = "24" };

    /// <summary>
    /// Null when the document is an svg on a square <c>0 0 S S</c> canvas whose first element is
    /// the full-bleed rounded tile (<c>rect width=S height=S</c> with a fill) followed by at least
    /// one glyph path — the shape every shipped node glyph has, so it reads on both themes.
    /// </summary>
    private static string? TileProblem(XDocument doc, string size)
    {
        XNamespace svg = "http://www.w3.org/2000/svg";
        var root = doc.Root;
        if (root is null || root.Name != svg + "svg")
            return "root is not an svg element";

        var viewBox = ((string?)root.Attribute("viewBox"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (viewBox is not ["0", "0", var w, var h] || w != size || h != size)
            return $"viewBox is not '0 0 {size} {size}': '{root.Attribute("viewBox")?.Value}'";

        var tile = root.Elements().FirstOrDefault();
        if (tile is null || tile.Name != svg + "rect"
            || (string?)tile.Attribute("width") != w
            || (string?)tile.Attribute("height") != h
            || string.IsNullOrEmpty((string?)tile.Attribute("fill")))
            return "first element is not the full-bleed filled tile rect";

        if (!root.Elements().Skip(1).Any())
            return "no glyph drawn on the tile";

        return null;
    }

    /// <summary>The vendored Fluent glyphs carry their upstream MIT notice in the shipped assembly, and every file
    /// the notice names is a glyph that ships (the notice cannot silently drift from the files it covers).</summary>
    [Fact]
    public void TheFluentNotice_ShipsWithTheGlyphs()
    {
        var assembly = typeof(NodeIconCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("MeshWeaver.Graph.ThirdPartyNotices.fluentui-system-icons.txt");
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        var text = reader.ReadToEnd();

        Assert.Contains("MIT License", text);
        Assert.Contains("Copyright (c) 2020 Microsoft Corporation", text);
        Assert.Contains("Permission is hereby granted", text);
        var files = text.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(".svg", StringComparison.Ordinal)).ToList();
        Assert.True(files.Count >= 100, $"expected the vendored glyphs to be listed, found {files.Count}");
        foreach (var file in files)
            Assert.NotNull(assembly.GetManifestResourceStream(IconResourcePrefix + file));
    }
}
