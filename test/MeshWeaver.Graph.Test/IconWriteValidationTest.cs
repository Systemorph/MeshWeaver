using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The icon rule (<see cref="IconSafety"/>) as pure logic: every shape the platform's own content
/// uses is accepted, and every value that could only be active content is refused BY NAME.
///
/// <para>The refused fixtures are inert stand-ins (<c>void 0</c>): what is under test is that the
/// rule recognises the SHAPE, which it does from the parsed element tree, not from what a script
/// would do.</para>
/// </summary>
public class IconSafetyTest
{
    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    /// <summary>The shapes the core, plugin and satellite node trees use — each must pass.</summary>
    public static TheoryData<string> Accepted() =>
    [
        "",
        "Document",
        "MusicNote2",            // a Fluent name with a digit reads as text — still fine
        "code",
        "🤖",
        "👩‍💼",                  // ZWJ sequence
        "/static/NodeTypeIcons/box.svg",
        "/api/content/MeshWeaver/logo.svg",
        "./assets/icon.png",
        "https://example.com/logo.png?size=64&theme=dark",
        "content:icon.svg",
        "content/icon.png",
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",
        $"<svg {Ns} viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"><circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M4 4h16\"/></svg>",
        $"<svg {Ns} width=\"24\" height=\"24\"><defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#fff\"/></linearGradient></defs><rect width=\"24\" height=\"24\" rx=\"6\" fill=\"url(#g)\" data-mw-mark=\"1\"/><text x=\"4\" y=\"16\" font-size=\"10\">A</text></svg>",
        $"<svg {Ns} xmlns:xlink=\"http://www.w3.org/1999/xlink\"><defs><path id=\"p\" d=\"M0 0h1\"/></defs><use xlink:href=\"#p\"/><use href=\"#p\"/></svg>",
        "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%2024%2024'%3E%3Cpath%20d='M4%204h16'/%3E%3C/svg%3E",
        "data:image/svg+xml;utf8,%3Csvg%20xmlns='http://www.w3.org/2000/svg'/%3E",
        "data:image/svg+xml;charset=utf-8,%3Csvg%20xmlns='http://www.w3.org/2000/svg'/%3E",
        "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnLz4=",
    ];

    [Theory]
    [MemberData(nameof(Accepted))]
    public void AcceptedShapes_Pass(string icon)
        => IconSafety.Refusal(icon).Should().BeNull($"'{icon}' is a shape the platform's own content uses");

    [Fact]
    public void NoIcon_Passes() => IconSafety.Refusal(null).Should().BeNull();

    /// <summary>
    /// The refusal a write boundary records is KEYED, and the catalog's English renders exactly the
    /// sentence the validator returns — so the two surfaces can never drift.
    /// </summary>
    [Fact]
    public void TheLocalizedRefusal_IsKeyed_AndTheCatalogRendersTheSameEnglish()
    {
        const string path = "TestData/n";
        const string detail = "the element <title> is not allowed in an SVG icon";
        var localized = IconSafety.LocalizedRefusal(path, detail);
        localized.Key.Should().Be(IconSafety.RefusalMessageKey);
        localized.English.Should().Be(IconSafety.RefusalMessage(path, detail));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "MeshWeaver.Messaging.Hub", "Localization", "strings.en.json")));
        var template = catalog.RootElement.GetProperty(IconSafety.RefusalMessageKey).GetString()!;
        template.Replace("{path}", path).Replace("{detail}", detail)
            .Should().Be(IconSafety.RefusalMessage(path, detail));
    }

    /// <summary>Each refused value, and the word the refusal must NAME.</summary>
    public static TheoryData<string, string> Refused() => new()
    {
        { $"<svg {Ns}><script>void 0</script></svg>", "<script>" },
        { $"<svg {Ns}><foreignObject width=\"1\" height=\"1\"/></svg>", "<foreignObject>" },
        { $"<svg {Ns} onload=\"void 0\"><path d=\"M0 0\"/></svg>", "onload" },
        { $"<svg {Ns}><path ONCLICK=\"void 0\" d=\"M0 0\"/></svg>", "ONCLICK" },
        { $"<svg {Ns}><set attributeName=\"fill\" to=\"red\"/></svg>", "<set>" },
        { $"<svg {Ns}><animate attributeName=\"x\"/></svg>", "<animate>" },
        { $"<svg {Ns}><image href=\"x.png\"/></svg>", "<image>" },
        { $"<svg {Ns}><a href=\"#x\"><path d=\"M0 0\"/></a></svg>", "<a>" },
        { $"<svg {Ns}><use href=\"https://example.com/sprite.svg#a\"/></svg>", "href" },
        { $"<svg {Ns} xmlns:xlink=\"http://www.w3.org/1999/xlink\"><use xlink:href=\"java&#9;script:void 0\"/></svg>", "xlink:href" },
        { $"<svg {Ns}><rect style=\"fill:url(https://example.com/a.svg#g)\"/></svg>", "'style'" },
        { $"<svg {Ns}><rect fill=\"url( https://example.com/a )\"/></svg>", "outside the icon" },
        { $"<svg {Ns}><rect fill=\"url( 'https://example.com/a' )\"/></svg>", "outside the icon" },
        { $"<svg {Ns}><rect class=\"x\" style=\"background:java script:void 0\"/></svg>", "style" },
        { $"<svg {Ns}><x:div xmlns:x=\"http://www.w3.org/1999/xhtml\"/></svg>", "namespace" },
        { $"<?xml-stylesheet href=\"a.css\"?><svg {Ns}/>", "processing instruction" },
        { $"<!DOCTYPE svg [<!ENTITY e \"x\">]><svg {Ns}>&e;</svg>", "not well-formed" },
        { $"<svg {Ns}><path d=\"M0 0\"></svg>", "not well-formed" },
        { "<img src=\"x\">", "<svg>" },
        { "<b>bold</b>", "<svg>" },
        { "javascript:void 0", "javascript" },
        { "JaVaScRiPt:void 0", "JaVaScRiPt" },
        { "vbscript:x", "vbscript" },
        { "data:text/html;base64,PGI+", "text/html" },
        { "data:image/png,notbase64", "base64" },
        { "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20onload='void%200'/%3E", "onload" },
        { "data:image/svg+xml,<svg/>", "encoded" },
        { "https://example.com/a.png\" x=\"", "'\"'" },
        { "/static/a b.png", "whitespace" },
        { "a<b", "'<'" },
        { "Tom & Jerry", "'&'" },
        { new string('x', IconSafety.MaxTextLength + 1), "at most" },
        // Review on #6910: the raster header was unchecked, so it could carry quotes and spaces
        // that end the attribute a renderer places the URI in.
        { "data:image/png;x=\" onerror=\"void 0\" x=\";base64,AAAA", "exactly" },
        { "data:image/png;charset=utf-8;base64,AAAA", "exactly" },
        { "data:image/png;base64,AA AA", "exactly" },
        // …and CSS escapes would hide a function name from the url( check.
        { $"<svg {Ns}><rect style=\"fill:u\\72l(https://example.com/p.svg#g)\"/></svg>", "'style'" },
        // Review round 2 on #6910 — the exact bypass strings:
        // (a) mutation through an HTML integration point: CDATA inside <title> is ignored by XML
        //     and re-parsed as markup by HTML.
        { "<svg xmlns=\"http://www.w3.org/2000/svg\"><title><g><![CDATA[><img src=x onerror=\"void 0\">]]></g></title></svg>", "<title>" },
        { $"<svg {Ns}><g><![CDATA[><img src=x onerror=\"void 0\">]]></g></svg>", "CDATA" },
        { $"<svg {Ns}><desc>x</desc></svg>", "<desc>" },
        { $"<svg {Ns}><!-- x --><path d=\"M0 0\"/></svg>", "comment" },
        { $"<svg {Ns}><text>a &gt; b</text></svg>", "'<' or '>'" },
        // (b) CSS functions that load an image without a literal url(.
        { $"<svg {Ns}><rect style=\"mask-image:image-set('https://example.com/mask.png' 1x)\"/></svg>", "'style'" },
        { $"<svg {Ns}><rect style=\"background-image:image-set('https://example.com/mask.png' 1x)\"/></svg>", "'style'" },
        { $"<svg {Ns}><rect fill=\"image-set(x 1x)\"/></svg>", "image-set(" },
        { $"<svg {Ns}><rect fill=\"cross-fade(x)\"/></svg>", "cross-fade(" },
        // Review round 3 on #6910 — the SVG data-URI header: a renderer decodes '&comma;' into the
        // first comma and '#' makes the validated tail a fragment, so the header is matched exactly
        // and '&', '#' and ';' are refused in the payload.
        { "data:image/svg+xml;x=a&comma;%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20onload='void%200'/%3E#,%3Csvg/%3E", "exactly one of" },
        { "data:image/svg+xml;foo=bar,%3Csvg%20xmlns='http://www.w3.org/2000/svg'/%3E", "exactly one of" },
        { "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'/%3E#x", "'#'" },
        { "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'/%3E&amp;", "'&'" },
        { "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'/%3E;x", "';'" },
        // Review round 4 on #6910 — Unicode whitespace: XML decodes &#160; to U+00A0, which a
        // TrimStart skips and a URL / CSS parser keeps, so the target was a relative resource.
        { $"<svg {Ns}><defs><path id=\"p\" d=\"M0 0\"/></defs><use href=\"&#160;#p\"/></svg>", "fragment" },
        { $"<svg {Ns} xmlns:xlink=\"http://www.w3.org/1999/xlink\"><defs><path id=\"p\" d=\"M0 0\"/></defs><use xlink:href=\"&#160;#p\"/></svg>", "fragment" },
        { $"<svg {Ns}><rect fill=\"url(&#160;#p)\"/></svg>", "outside the icon" },
        { $"<svg {Ns}><rect fill=\"red&#160;\"/></svg>", "non-ASCII" },
        // (c) a whitespace-only value past the length bound.
        { new string(' ', IconSafety.MaxIconLength + 1), "at most" },
        { $"<svg {Ns}><rect fill=\"u\\72l(https://example.com/p.svg#g)\"/></svg>", "backslash" },
    };

    /// <summary>
    /// The whole value is bounded before any rule reads it, so a write cannot buy unbounded work:
    /// an attribute repeating <c>url(#g)</c> past the bound is refused outright.
    /// </summary>
    [Fact]
    public void AnIconPastTheLengthBound_IsRefused_BeforeAnyParse()
    {
        var huge = $"<svg {Ns}><rect fill=\"{string.Concat(Enumerable.Repeat("url(#g)", 100_000))}\"/></svg>";
        IconSafety.Refusal(huge).Should().Contain("at most");
    }

    /// <summary>An attribute longer than its own bound is refused, naming it.</summary>
    [Fact]
    public void AnAttributePastItsBound_IsRefused()
    {
        var longValue = string.Concat(Enumerable.Repeat("url(#g) ", IconSafety.MaxAttributeLength / 8 + 1));
        IconSafety.Refusal($"<svg {Ns}><rect fill=\"{longValue}\"/></svg>").Should().Contain("'fill'");
    }

    /// <summary>Many fragment references within the bounds are fine — and judged in one pass.</summary>
    [Fact]
    public void ManyFragmentReferencesWithinTheBounds_Pass()
    {
        var value = string.Concat(Enumerable.Repeat("url(#g) ", 7_000));
        IconSafety.Refusal($"<svg {Ns}><rect fill=\"{value}\"/></svg>").Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void RefusedShapes_AreRefused_ByName(string icon, string named)
    {
        var refusal = IconSafety.Refusal(icon);
        refusal.Should().NotBeNull($"'{icon}' could only ever be active content, or escapes its context");
        refusal!.Should().Contain(named, "the refusal must name what it refused, so the writer can fix it");
    }

    /// <summary>
    /// 🚨 The external-reference rule is not a blanket ban on <c>url(</c>: a fragment reference is
    /// how every gradient icon in the content trees fills its shape.
    /// </summary>
    [Fact]
    public void FragmentReferences_AreNotExternalReferences()
        => IconSafety.Refusal($"<svg {Ns}><rect fill=\"url(#g)\" clip-path=\"url( #c)\" transform=\"rotate(45 12 12) translate(1,2)\" stroke=\"rgb(10, 20, 30)\"/></svg>").Should().BeNull();
}

/// <summary>
/// <see cref="IconMarkupValidator"/>'s CHANGE-not-state rule, as pure logic: a stored icon that
/// predates the rule must not make its node un-editable.
/// </summary>
public class IconMarkupValidatorJudgeTest
{
    private const string Active = "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"void 0\"/>";

    private static MeshNode Node(string? icon) => new("n", "TestData") { Name = "n", Icon = icon };

    [Fact]
    public void Create_WithARefusedIcon_IsRefused_ValidationFailed()
    {
        var result = IconMarkupValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = Node(Active)
        });
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.ValidationFailed);
        result.ErrorMessage.Should().Contain("TestData/n").And.Contain("onload");
    }

    [Fact]
    public void Update_ThatLeavesAStoredIconAsItIs_Passes_WhateverItHolds()
        => IconMarkupValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = Node(Active) with { Name = "renamed" },
            ExistingNode = Node(Active)
        }).IsValid.Should().BeTrue(
            "a node whose icon predates the rule must stay editable — the rule judges a CHANGE");

    [Fact]
    public void Update_ThatChangesTheIconToARefusedValue_IsRefused()
        => IconMarkupValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = Node(Active),
            ExistingNode = Node("Document")
        }).IsValid.Should().BeFalse();

    [Fact]
    public void Update_ThatReplacesAStoredRefusedIcon_WithAnAcceptedOne_Passes()
        => IconMarkupValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = Node("Document"),
            ExistingNode = Node(Active)
        }).IsValid.Should().BeTrue("replacing a bad icon is the repair and must stay open");
}

/// <summary>
/// The import paths must keep working: every icon in the node trees this repository ships (the
/// embedded documentation and the samples) passes the rule. Markdown is read through the importer's
/// own <see cref="MarkdownFileParser"/>, so the icon judged is the one the import would write.
/// </summary>
public class IconCorpusTest(ITestOutputHelper output)
{
    [Fact]
    public void EveryShippedIcon_PassesTheRule()
    {
        var root = FindRepoRoot();
        var trees = new[]
        {
            Path.Combine(root, "src", "MeshWeaver.Documentation", "Data"),
            Path.Combine(root, "samples"),
        };
        var parser = new MarkdownFileParser();
        var icons = new List<(string File, string Icon)>();
        foreach (var tree in trees.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(tree, "*.md", SearchOption.AllDirectories))
            {
                var node = parser.Parse(file, File.ReadAllText(file), Path.GetRelativePath(tree, file));
                if (!string.IsNullOrEmpty(node?.Icon))
                    icons.Add((file, node.Icon));
            }
            foreach (var file in Directory.EnumerateFiles(tree, "*.json", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                if (TopLevelIcon(file) is { Length: > 0 } icon)
                    icons.Add((file, icon));
            }
        }

        output.WriteLine($"judged {icons.Count} icons across {string.Join(", ", trees.Select(t => Path.GetRelativePath(root, t)))}");
        icons.Count.Should().BeGreaterThan(100,
            "a corpus walk that found (almost) nothing proves nothing — the trees carry thousands of icons");

        var refused = icons
            .Select(i => (i.File, Refusal: IconSafety.Refusal(i.Icon)))
            .Where(i => i.Refusal is not null)
            .Select(i => $"{Path.GetRelativePath(root, i.File)}: {i.Refusal}")
            .ToList();
        refused.Should().BeEmpty("every icon this repository ships must pass, or its import would be refused");
    }

    private static string? TopLevelIcon(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.NameEquals("icon") || property.NameEquals("Icon"))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            return null;
        }
        catch (JsonException)
        {
            return null; // not a node file
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Review round 2 on #6910: the validator judges the change against the node it READ, and another
/// writer can move the node before the write applies. So <c>UpdateNode</c> and the upsert re-judge
/// the icon INSIDE the write, against the live node. This class proves that second check stands on
/// its own: <see cref="IconMarkupValidator"/> is removed from the mesh, and a refused icon is still
/// refused at the write — the guarantee that covers the read-then-write window.
/// </summary>
public class IconWriteLambdaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Active = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>void 0</script></svg>";
    private const string Clean = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16\"/></svg>";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services =>
        {
            foreach (var d in services.Where(d => d.ImplementationType == typeof(IconMarkupValidator)).ToList())
                services.Remove(d);
            return services;
        });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static MeshNode Node(string id, string? icon) => new(id, TestPartition)
    {
        Name = "Icon " + id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        Icon = icon,
    };

    [Fact(Timeout = 180_000)]
    public async Task UpdateNode_ReJudgesTheIconAgainstTheLiveNode_InsideTheWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<IconMarkupValidator>()
            .Should().BeEmpty("precondition: this class proves the in-write check WITHOUT the validator");

        var id = "il" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Node(id, Clean)).Take(1).Timeout(60.Seconds()).Await(ct);

        var failure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(Node(id, Active) with { Name = "renamed" })
                .Take(1).Timeout(60.Seconds()).Await(ct));
        failure.Should().NotBeNull("the write itself must refuse an icon change the live node does not hold");
        failure!.Message.Should().Contain("<script>");

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds()).Await(ct);
        after.Icon.Should().Be(Clean, "a refused write leaves the live node as it was");
    }

    [Fact(Timeout = 180_000)]
    public async Task UpdateNode_LeavingAStoredIconAsItIs_StillLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "il" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Node(id, Clean)).Take(1).Timeout(60.Seconds()).Await(ct);

        var updated = await MeshService.UpdateNode(Node(id, Clean) with { Name = "renamed" })
            .Take(1).Timeout(60.Seconds()).Await(ct);
        updated.Name.Should().Be("renamed", "the control: an update that keeps the icon is not judged");
    }
}

/// <summary>
/// The rule at the write boundaries a caller actually reaches: the creation chain, the
/// <c>UpdateNode</c> pipeline (behind the MCP <c>update</c>/<c>patch</c> tools) and the upsert's update
/// branch, which runs no validator chain and so carries the rule inline.
/// </summary>
public class IconWriteBoundaryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Active = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>void 0</script></svg>";
    private const string Clean = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16\"/></svg>";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewId() => "ic" + Guid.NewGuid().ToString("N")[..8];

    private static MeshNode Node(string id, string? icon) => new(id, TestPartition)
    {
        Name = "Icon " + id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        Icon = icon,
    };

    [Fact]
    public void AddGraph_RegistersTheValidator()
        => Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<IconMarkupValidator>()
            .Should().ContainSingle("a validator that is correct but unwired protects nothing");

    [Fact(Timeout = 180_000)]
    public async Task Create_WithARefusedIcon_IsRefused_AndWritesNothing()
    {
        var id = NewId();
        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(Node(id, Active))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull("an icon that could only be active content must not reach the store");
        failure!.Message.Should().Contain("<script>", "the refusal names what it refused");

        var listed = await MeshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{TestPartition} nodeType:Markdown"))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);
        listed.Items.Should().NotContain(n => n.Id == id, "a refused create must leave no node behind");
    }

    [Fact(Timeout = 180_000)]
    public async Task Create_WithACleanSvgIcon_Lands()
    {
        var id = NewId();
        var created = await MeshService.CreateNode(Node(id, Clean))
            .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);
        created.Icon.Should().Be(Clean, "the control: a drawing-only SVG is exactly what icons are");
    }

    [Fact(Timeout = 180_000)]
    public async Task Update_ThatChangesTheIconToARefusedValue_IsRefused_AndLeavesTheNodeAsItWas()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Node(id, Clean))
            .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(Node(id, Active))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));
        failure.Should().NotBeNull("UpdateNode is what the MCP update and patch tools call");
        failure!.Message.Should().Contain("<script>");

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);
        after.Icon.Should().Be(Clean, "a refused update must leave the node as it was");
    }

    [Fact(Timeout = 180_000)]
    public async Task Upsert_ThatChangesTheIconToARefusedValue_IsRefused_ValidationFailed()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Node(id, Clean))
            .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(new CreateOrUpdateNodeRequest(Node(id, Active))))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"upsert {path}: success={response.Success} reason={response.RejectionReason} error={response.Error}");

        response.Success.Should().BeFalse(
            "the upsert's update branch runs no validator chain — every importer and installer "
            + "arrives here, so it must carry the rule itself");
        response.RejectionReason.Should().Be(NodeUpsertRejectionReason.ValidationFailed,
            "a rule these bytes break is a content verdict, so an importer does not retry it forever");
        response.Error.Should().Contain("<script>");

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);
        after.Icon.Should().Be(Clean);
    }

    /// <summary>
    /// Review on #6910: the upsert's unchanged-icon exemption compares against the LIVE node the
    /// merge is applied to, not the durable row the handler read — that row can trail the live node
    /// during the persistence debounce, and a stored refused icon the owner has since repaired would
    /// then read as "unchanged" and be written back over the repair.
    ///
    /// <para>🚨 A behaviour pin, NOT a reproduction of the trail: in this test host the handler's
    /// row read already reflects the repair (measured — the earlier snapshot comparison passes this
    /// test too), so the window cannot be opened deterministically here. What it pins is the
    /// outcome the fix guarantees by construction: repair, then an upsert re-asserting the legacy
    /// icon, is refused and the repair stands.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Upsert_CarryingARefusedIcon_IsJudgedAgainstTheLiveNode_NotATrailingDurableRow()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        var ct = TestContext.Current.CancellationToken;
        await MeshService.CreateNode(Node(id, Clean))
            .Take(1).Timeout(60.Seconds()).Await(ct);

        var store = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var durable = await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => store.Read(path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Icon == Clean)
            .FirstAsync().Timeout(60.Seconds()).Await(ct);

        // A legacy stored icon: written to the row directly, as content that predates the rule.
        // The owner adopts it from the change feed.
        await store.Write(durable! with { Icon = Active }, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(60.Seconds()).Await(ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Icon == Active).FirstAsync().Timeout(60.Seconds()).Await(ct);

        // The owner REPAIRS it. The durable row trails the live node until the persistence
        // debounce flushes, and the upsert below reads that row in the window.
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Icon = Clean })
            .Take(1).Timeout(60.Seconds()).Await(ct);
        var trailing = await store.Read(path, Mesh.JsonSerializerOptions).Take(1).Timeout(60.Seconds()).Await(ct);
        Output.WriteLine($"durable row still holds the refused icon: {trailing?.Icon == Active}");

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(
                new CreateOrUpdateNodeRequest(Node(id, Active) with { Name = "renamed" })))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        Output.WriteLine($"upsert {path}: success={response.Success} reason={response.RejectionReason} error={response.Error}");

        response.Success.Should().BeFalse(
            "the live node holds the clean icon, so writing the refused one is a CHANGE and must be judged");
        response.RejectionReason.Should().Be(NodeUpsertRejectionReason.ValidationFailed);

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds())
            .Await(ct);
        after.Icon.Should().Be(Clean, "the repaired icon must not be overwritten");
    }
}
