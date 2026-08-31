using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The naming rules of <see cref="ManifestResourceNames"/>, pinned one input at a time.
///
/// <para>🚨 <b>Every expectation in this file is a MEASUREMENT, not a derivation.</b> Each was read
/// out of an assembly the real .NET SDK emitted, with
/// <c>System.Reflection.Metadata</c>'s manifest-resource table — the probe projects are named in the
/// comments. That matters because the rules are not guessable and a wrong one is silent: the
/// directory is mangled and the file name is not, a leading digit is PREFIXED rather than replaced,
/// a dot inside a directory name survives as a dot, and a segment that reduces to a single
/// underscore is DOUBLED.</para>
///
/// <para><see cref="EmbeddedResourceTest"/> then proves the rules end to end against a real
/// <c>dotnet build</c>. This file is the microscope; that one is the control.</para>
/// </summary>
public class ManifestResourceNamesTest
{
    // ── the Everett mangling of a directory path ───────────────────────────────────────────────

    /// <summary>
    /// The mangling table. Every row was produced by building a probe project with those exact
    /// directories and reading the emitted names back (probe p2); the <c>--</c> and <c>_</c> rows
    /// were established by the SDK build FAILING with <c>CS1508: Resource identifier 'R.__.F.md' has
    /// already been used</c>, which is only possible if both mangle to <c>__</c>.
    /// </summary>
    /// <param name="directory">The directory path as it is on disk.</param>
    /// <param name="expected">The dotted, mangled identifier the SDK produced.</param>
    [Theory]
    [InlineData("Data", "Data")]
    [InlineData("Data/Nested", "Data.Nested")]
    [InlineData("with-dash", "with_dash")]                  // an invalid character becomes '_'
    [InlineData("9digits", "_9digits")]                     // a leading digit is PREFIXED
    [InlineData("space dir", "space_dir")]
    [InlineData("Dot.Dir", "Dot.Dir")]                      // a dot is a SEPARATOR, not a character
    [InlineData("Dot.9Dir", "Dot._9Dir")]                   // …so each half is mangled on its own
    [InlineData("--", "__")]
    [InlineData("_", "__")]                                 // a lone underscore is DOUBLED
    [InlineData("ü-dir", "ü_dir")]                          // a letter is a letter, ASCII or not
    [InlineData("x y.z-w", "x_y.z_w")]
    [InlineData("deep/a-b/9c", "deep.a_b._9c")]
    [InlineData("", "")]
    public void EveryMangledDirectorySegmentMatchesWhatTheSdkEmitted(string directory, string expected) =>
        ManifestResourceNames.MakeValidEverettIdentifier(directory).Should().Be(expected);

    /// <summary>
    /// 🚨 The FILE name is never mangled — the asymmetry that makes this whole file necessary.
    /// <c>Weird-File.Name.md</c> in a <c>with-dash</c> directory keeps its own hyphen and dot while
    /// the directory loses both (probe p1).
    /// </summary>
    [Fact]
    public void TheFileNameIsCarriedVERBATIMWhileTheDirectoryIsMangled() =>
        ManifestResourceNames.Compute("Probe.Root.Ns", Path.Combine("with-dash", "Weird-File.Name.md"))
            .Should().Be("Probe.Root.Ns.with_dash.Weird-File.Name.md");

    /// <summary>An empty root namespace contributes no prefix and no leading dot (probe p5).</summary>
    [Fact]
    public void AnEmptyRootNamespaceContributesNothing() =>
        ManifestResourceNames.Compute("", Path.Combine("d", "In.md")).Should().Be("d.In.md");

    /// <summary>A file at the project root has no directory part and therefore no extra dot.</summary>
    [Fact]
    public void AFileAtTheRootHasNoDirectorySegment() =>
        ManifestResourceNames.Compute("R", "Top.md").Should().Be("R.Top.md");

    /// <summary>
    /// A <c>.resources</c> file needs no special case: the SDK's strip-and-re-append branch is
    /// arithmetically identical to the plain rule for an input already carrying that extension, and
    /// it mangles the directory in that branch too (probe p11: <c>r-dir\Bin.resources</c> →
    /// <c>RB.r_dir.Bin.resources</c>).
    /// </summary>
    [Fact]
    public void ADotResourcesFileFollowsTheSamePathRuleAsAnythingElse() =>
        ManifestResourceNames.Compute("RB", Path.Combine("r-dir", "Bin.resources"))
            .Should().Be("RB.r_dir.Bin.resources");

    // ── the target path the name is computed FROM ──────────────────────────────────────────────

    /// <summary>
    /// <c>%(TargetPath)</c> wins outright, then <c>%(Link)</c>, then the item spec — the order the
    /// SDK's <c>AssignTargetPath</c> applies (probes p9/p10), and the reason a <c>Link</c> renames a
    /// resource that is sitting inside the project already.
    /// </summary>
    [Fact]
    public void TargetPathBeatsLinkWhichBeatsTheItemSpec()
    {
        var directory = Path.Combine(Path.GetTempPath(), "proj");
        var file = Path.Combine(directory, "inner", "F.md");

        ManifestResourceNames.TargetPathFor(directory, Path.Combine("inner", "F.md"), file,
            link: Path.Combine("ignored", "me.md"), targetPath: Path.Combine("a-b", "c.md"))
            .Should().Be(Path.Combine("a-b", "c.md"));

        ManifestResourceNames.TargetPathFor(directory, Path.Combine("inner", "F.md"), file,
            link: Path.Combine("re", "named", "G.md"), targetPath: null)
            .Should().Be(Path.Combine("re", "named", "G.md"));

        ManifestResourceNames.TargetPathFor(directory, Path.Combine("inner", "F.md"), file,
            link: null, targetPath: null)
            .Should().Be(Path.Combine("inner", "F.md"));
    }

    /// <summary>
    /// 🚨 A file OUTSIDE the project with no <c>Link</c> loses its directory ENTIRELY, rather than
    /// keeping a <c>..</c> that would mangle into something. Measured (probe p4):
    /// <c>Include="..\shared\Shared.md"</c> emitted <c>ProjNameDiffers.Shared.md</c>.
    /// </summary>
    [Fact]
    public void AFileOutsideTheProjectFallsAllTheWayBackToItsBareFileName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "proj");
        var outside = Path.GetFullPath(Path.Combine(directory, "..", "shared", "Shared.md"));

        ManifestResourceNames.TargetPathFor(
                directory, Path.Combine("..", "shared", "Shared.md"), outside, link: null, targetPath: null)
            .Should().Be("Shared.md");
    }

    /// <summary>An ABSOLUTE item spec inside the project resolves back to its relative path.</summary>
    [Fact]
    public void AnAbsoluteItemSpecInsideTheProjectBecomesItsRelativePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "proj");
        var file = Path.Combine(directory, "Abs.md");

        ManifestResourceNames.TargetPathFor(directory, file, file, link: null, targetPath: null)
            .Should().Be("Abs.md");
    }

    // ── the culture that routes a resource out of the assembly ─────────────────────────────────

    /// <summary>
    /// Which second extensions are cultures, and which merely look like one. <c>de</c> and
    /// <c>de-DE</c> sent probe p1's files into <c>de/</c> and <c>de-DE/</c> satellite assemblies;
    /// <c>zz</c> and <c>notaculture</c> left theirs in the main assembly.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="expected">The culture the SDK would assign, or null.</param>
    [Theory]
    [InlineData("Foo.de.md", "de")]
    [InlineData("Foo.de-DE.md", "de-DE")]
    [InlineData("strings.en.json", "en")]
    [InlineData("Foo.zz.md", null)]
    [InlineData("Foo.notaculture.md", null)]
    [InlineData("Plain.md", null)]
    [InlineData("Weird-File.Name.md", null)]
    public void ACultureIsRecognisedExactlyWhereTheSdkRecognisesOne(string fileName, string? expected)
    {
        Assert.SkipUnless(ManifestResourceNames.CanDecideCulture,
            "this process reports no predefined culture even for 'de' (invariant globalization), which "
            + "is the condition ProjectFile refuses under rather than guessing — there is nothing to "
            + "assert about a decision that is not being made.");
        ManifestResourceNames.CultureOf(fileName).Should().Be(expected);
    }

    /// <summary>
    /// The invariant-globalization self-check itself. Under a normal runtime it is true, and this
    /// asserts the probe is asking a question with a knowable answer — if <c>de</c> ever stops
    /// being a predefined culture, the refusal path in <see cref="ProjectFile"/> takes over and
    /// nothing is embedded under a guessed name.
    /// </summary>
    [Fact]
    public void TheCultureCapabilityIsPROBEDRatherThanAssumed()
    {
        ManifestResourceNames.CanDecideCulture.Should().Be(
            ManifestResourceNames.IsValidCultureString("de")
            && ManifestResourceNames.IsValidCultureString("de-DE"));
        ManifestResourceNames.IsValidCultureString("notaculture").Should().BeFalse();
        ManifestResourceNames.IsValidCultureString("").Should().BeFalse();
    }

    /// <summary>Only a base name with its own extension can possibly carry a culture.</summary>
    [Theory]
    [InlineData("Foo.de.md", true)]
    [InlineData("Foo.md", false)]
    [InlineData("Foo", false)]
    public void ADottedBaseNameIsTheOnlyThingThatCanCarryACulture(string fileName, bool dotted) =>
        ManifestResourceNames.HasDottedBaseName(fileName).Should().Be(dotted);
}
