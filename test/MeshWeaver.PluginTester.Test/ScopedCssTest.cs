using System.Reactive;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// CSS isolation in <c>build-project</c> — the whole chain, pinned against the REAL SDK:
/// <see cref="ScopedCss.GenerateScope"/> against scope values measured from an SDK build of
/// MeshWeaver.Blazor, <see cref="ScopedCss.Rewrite"/> against VERBATIM <c>.rz.scp.css</c> outputs
/// the SDK produced for the same inputs, and the executing build against both halves of the
/// contract at once: the scope stamped into the compiled markup IS the scope in the emitted
/// <c>wwwroot/&lt;Name&gt;.styles.css</c>.
///
/// <para>🚨 Every expected string here is SDK OUTPUT, not this repo's opinion. A rewriter that
/// drifts from the SDK produces stylesheets whose selectors match nothing — the page renders,
/// unstyled, with empty logs (#2221's signature) — so fidelity is asserted byte-for-byte. The
/// full-corpus run (all 21 scoped stylesheets across five module projects, 2026-08-31) was
/// byte-identical; these embedded pairs are the durable representatives: one dense synthetic
/// (::deep leading and infix, pseudo-classes before pseudo-elements, @media recursion, selector
/// lists, :not()) and one production file (@keyframes rename + animation shorthand references).</para>
/// </summary>
public class ScopedCssTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-scopedcss-{Guid.NewGuid():N}");

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string AppDirectory()
    {
        var app = Path.Combine(_root, "_container");
        if (Directory.Exists(app))
            return app;
        Directory.CreateDirectory(app);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "mw-plugin-test.deps.json"),
            Path.Combine(app, "mw-plugin-test.deps.json"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(app, "MeshWeaver.ShortGuid.dll"));
        return app;
    }

    // ── the scope hash: pinned to values MEASURED from an SDK build of MeshWeaver.Blazor ────────

    [Theory]
    [InlineData("Components/CodeBlock.razor.css", "MeshWeaver.Blazor", "b-h3pg7owarf")]
    [InlineData("Components/DialogView.razor.css", "MeshWeaver.Blazor", "b-qf8u66cq1w")]
    [InlineData("FileExplorer/FileBrowser.razor.css", "MeshWeaver.Blazor", "b-1v0kb17jfa")]
    public void TheScopeHashMatchesTheSdk(string relativePath, string targetName, string expected)
        => ScopedCss.GenerateScope(relativePath, targetName).Should().Be(expected,
            "the generator-stamped attribute and an SDK-built neighbour must agree; a from-memory "
            + "hash was the reason this used to be a refusal");

    [Fact]
    public void TheScopeHashIsSeparatorAndCaseInsensitive()
        => ScopedCss.GenerateScope(@"Components\CodeBlock.RAZOR.css", "MeshWeaver.Blazor")
            .Should().Be("b-h3pg7owarf", "the SDK lowercases and the path arrives OS-flavoured");

    // ── the rewriter: verbatim SDK outputs ───────────────────────────────────────────────────────

    /// <summary>The dense synthetic, SDK-built 2026-08-31 (scope b-jiiuxwrs1r for Card.razor.css
    /// under Widgets.CssProbe): ::deep leading and infix — note the SDK's double space where an
    /// infix ::deep is excised — pseudo-class before pseudo-element, @media recursion, lists.</summary>
    private const string ProbeSource = """
.card, .panel > .row + .cell {
    color: red;
}
.card::before {
    content: "*";
}
.card:hover::after {
    content: "!";
}
::deep .external {
    margin: 0;
}
.card ::deep .inner, ::deep.attached>li {
    padding: 0;
}
@media (max-width: 700px) {
    .card:not(.wide) {
        display: none;
    }
    li::marker {
        color: blue;
    }
}
""";

    private const string ProbeSdkOutput = """
.card[b-jiiuxwrs1r], .panel > .row + .cell[b-jiiuxwrs1r] {
    color: red;
}
.card[b-jiiuxwrs1r]::before {
    content: "*";
}
.card:hover[b-jiiuxwrs1r]::after {
    content: "!";
}
[b-jiiuxwrs1r] .external {
    margin: 0;
}
.card[b-jiiuxwrs1r]  .inner, [b-jiiuxwrs1r].attached>li {
    padding: 0;
}
@media (max-width: 700px) {
    .card:not(.wide)[b-jiiuxwrs1r] {
        display: none;
    }
    li[b-jiiuxwrs1r]::marker {
        color: blue;
    }
}
""";

    /// <summary>Production file (MeshWeaver.Blazor's NamedAreaView.razor.css, scope
    /// b-r8wvn3lp5k): @keyframes renamed with the scope suffix, the animation SHORTHAND's
    /// name reference follows, timing keywords (infinite, ease-in-out) untouched.</summary>
    private const string NamedAreaSource = """
.dots-spinner {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 8px;
}

.dots-spinner .dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background-color: var(--neutral-foreground-rest, currentColor);
    animation: dots-blink 1.4s infinite ease-in-out both;
}

.dots-spinner .dot:nth-child(1) {
    animation-delay: -0.32s;
}

.dots-spinner .dot:nth-child(2) {
    animation-delay: -0.16s;
}

.dots-spinner .dot:nth-child(3) {
    animation-delay: 0s;
}

@keyframes dots-blink {
    0%, 80%, 100% {
        opacity: 0.25;
        transform: scale(0.8);
    }
    40% {
        opacity: 0.8;
        transform: scale(1);
    }
}

.skeleton-placeholder {
    padding: 16px;
    border-radius: 12px;
    background: var(--neutral-layer-2);
    display: flex;
    flex-direction: column;
    gap: 8px;
    margin-bottom: 8px;
}

.skeleton-line {
    height: 12px;
    border-radius: 6px;
    background: var(--neutral-stroke-rest);
    opacity: 0.3;
    animation: skeleton-pulse 1.5s ease-in-out infinite;
}

@keyframes skeleton-pulse {
    0%, 100% { opacity: 0.3; }
    50% { opacity: 0.12; }
}
""";

    private const string NamedAreaSdkOutput = """
.dots-spinner[b-r8wvn3lp5k] {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 8px;
}

.dots-spinner .dot[b-r8wvn3lp5k] {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background-color: var(--neutral-foreground-rest, currentColor);
    animation: dots-blink-b-r8wvn3lp5k 1.4s infinite ease-in-out both;
}

.dots-spinner .dot:nth-child(1)[b-r8wvn3lp5k] {
    animation-delay: -0.32s;
}

.dots-spinner .dot:nth-child(2)[b-r8wvn3lp5k] {
    animation-delay: -0.16s;
}

.dots-spinner .dot:nth-child(3)[b-r8wvn3lp5k] {
    animation-delay: 0s;
}

@keyframes dots-blink-b-r8wvn3lp5k {
    0%, 80%, 100% {
        opacity: 0.25;
        transform: scale(0.8);
    }
    40% {
        opacity: 0.8;
        transform: scale(1);
    }
}

.skeleton-placeholder[b-r8wvn3lp5k] {
    padding: 16px;
    border-radius: 12px;
    background: var(--neutral-layer-2);
    display: flex;
    flex-direction: column;
    gap: 8px;
    margin-bottom: 8px;
}

.skeleton-line[b-r8wvn3lp5k] {
    height: 12px;
    border-radius: 6px;
    background: var(--neutral-stroke-rest);
    opacity: 0.3;
    animation: skeleton-pulse-b-r8wvn3lp5k 1.5s ease-in-out infinite;
}

@keyframes skeleton-pulse-b-r8wvn3lp5k {
    0%, 100% { opacity: 0.3; }
    50% { opacity: 0.12; }
}
""";

    [Fact]
    public void TheDenseSyntheticRewritesByteForByteLikeTheSdk()
        => ScopedCss.Rewrite(ProbeSource, "b-jiiuxwrs1r").Should().Be(ProbeSdkOutput);

    [Fact]
    public void KeyframesAndAnimationReferencesFollowTheSdkRename()
        => ScopedCss.Rewrite(NamedAreaSource, "b-r8wvn3lp5k").Should().Be(NamedAreaSdkOutput);

    [Fact]
    public void AnAtRuleOutsideTheProvenSubsetIsRefusedByName()
    {
        Action act = () => ScopedCss.Rewrite("@import url(x.css);\n.a { color: red; }", "b-0000000000");
        act.Should().Throw<InvalidOperationException>().WithMessage("*@import*",
            "a construct the rewriter cannot prove it reproduces must fail by name, never "
            + "half-rewrite into an unstyled page");
    }

    // ── the executing chain: one scope value, stamped in the markup AND in the stylesheet ───────

    [Fact]
    public async Task TheMarkupAndTheStylesheetCarryTheSameScope()
    {
        var entry = Write("Scoped/Scoped.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>Widgets.Scoped</AssemblyName>
                <RootNamespace>Widgets</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        Write("Scoped/Badge.razor", """
            <div class="badge">@Text</div>
            @code { [Microsoft.AspNetCore.Components.Parameter] public string? Text { get; set; } }
            """);
        Write("Scoped/Badge.razor.css", ".badge { color: red; }\n");
        Write("Scoped/wwwroot/js/badge.js", "// carried verbatim\n");

        var report = await ProjectBuild.Run(new()
        {
            EntryProject = entry,
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            MaxParallel = 2,
        }).Await(TestContext.Current.CancellationToken);

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        var result = report.Projects.Single().Result!;

        var scope = ScopedCss.GenerateScope("Badge.razor.css", "Widgets.Scoped");
        var outputDirectory = Path.GetDirectoryName(result.AssemblyPath!)!;

        // The stylesheet half: the aggregate exists, under the scope.
        var aggregate = File.ReadAllText(
            Path.Combine(outputDirectory, "wwwroot", "Widgets.Scoped.styles.css"));
        aggregate.Should().Contain($".badge[{scope}]");

        // The markup half: the SAME scope is a string literal in the compiled component —
        // Blazor string literals live in the #US heap as UTF-16, so that is what is probed.
        // Without this the stylesheet ships and matches NOTHING, silently.
        var image = File.ReadAllBytes(result.AssemblyPath!);
        image.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(scope)).Should().BeGreaterThan(0,
            "the generator must have received build_metadata.AdditionalFiles.CssScope");

        // The project's own wwwroot rides verbatim beside the aggregate.
        File.Exists(Path.Combine(outputDirectory, "wwwroot", "js", "badge.js")).Should().BeTrue();
    }
}
