using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <b>The gate SERVES a package's content assets — the coverage hole of #3424, closed.</b>
///
/// <para>A package ships its binaries as ordinary files under <c>{package}/content/**</c> — the
/// course videos and posters, the og cards, the fonts — and the installer publishes them by posting
/// ONE <c>SyncContentFilesRequest</c> to the package's partition ROOT. The handler for that message
/// is registered by <c>AddContentCollections()</c> and by nothing else. A PORTAL has it on every
/// per-node hub; the tester's mesh did not, and neither does the <c>Store/Plugin</c> NodeType most
/// package roots declare — so every root answered <i>"No handler found for message type
/// SyncContentFilesRequest"</i>, the publish was refused before the collection question was even
/// asked, and the gate NEVER ONCE exercised the path that serves a package's binaries. Measured on
/// two Education bakes on 2026-09-06: the same 15 packages, 30 warnings, exit 0.</para>
///
/// <para><b>What this test asserts, and why it is a read-back and not a count.</b> The publish
/// logs-and-continues by design, so "the installer did not complain" proves nothing. This runs the
/// REAL gate over a repo whose package carries two <c>content/**</c> assets and requires the gate's
/// own verdict to say <c>2/2 served</c> — where <i>served</i> means each asset resolved through
/// <c>ContentFileResolver</c> (the one server-side reading of a content reference, i.e. what a
/// course's <c>&lt;video src&gt;</c> actually goes through) and the collection had bytes at that
/// path. A package whose content lands in the wrong place publishes "successfully" and fails
/// here.</para>
///
/// <para>The negative control is in the same class: a repo carrying NO content assets must report
/// <c>0</c> carried and no error — so the check cannot pass by being inert, and cannot fail a
/// package that ships no binaries.</para>
/// </summary>
public class GateServesPackageContentTest(ITestOutputHelper output)
{
    private const string CourseIndexJson =
        """{"$type":"MeshNode","id":"Course","namespace":"","path":"Course","mainNode":"Course","name":"Course Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A package that ships binaries."}}""";

    private const string LessonJson =
        """{"$type":"MeshNode","id":"Lesson1","namespace":"Course","path":"Course/Lesson1","mainNode":"Course/Lesson1","name":"Lesson 1","nodeType":"Markdown","state":"Active","content":{"$type":"MarkdownContent","content":"# Lesson"}}""";

    // The two assets, in the two shapes ContentAssetMapper distinguishes: one owned by the Space
    // ROOT (`content/…` → collection-relative `videos/intro.mp4`) and one owned by a CHILD node
    // (`Lesson1/content/…` → collection-relative `Lesson1/poster.png`). A mount that only ever
    // resolved the root's own files would pass on the first and fail on the second.
    private const string RootAssetPath = "Course/content/videos/intro.mp4";
    private const string ChildAssetPath = "Course/Lesson1/content/poster.png";

    /// <summary>The collection-relative paths the two files above must be served at.</summary>
    private static readonly string[] ServedPaths = ["videos/intro.mp4", "Lesson1/poster.png"];

    [Fact(Timeout = 900_000)]
    public async Task ThePackagesCommittedBinaries_AreServedAfterTheGateInstallsIt()
    {
        var repo = TempDirectory("mw-3424-content");
        var bakeOut = TempDirectory("mw-3424-bake");
        try
        {
            Write(repo, "Course/index.json", CourseIndexJson);
            Write(repo, "Course/Lesson1.json", LessonJson);
            // Deliberately NOT valid UTF-8 — these ride as binary, exactly as a real .mp4/.png does.
            WriteBytes(repo, RootAssetPath, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0xFF]);
            WriteBytes(repo, ChildAssetPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF]);

            var log = new StringWriter();
            var report = await PluginGateRunner.Run(new GateOptions
            {
                RepoRoot = repo,
                Output = log,
                BakeOutputDirectory = bakeOut,
                SourceSha = "cafebabe",
                CompileTimeout = TimeSpan.FromMinutes(4),
                RenderTimeout = TimeSpan.FromMinutes(2),
            }).FirstAsync().Await();
            output.WriteLine(log.ToString());
            report.WriteSummary(new StringWriterAdapter(output));

            report.FatalError.Should().BeNull("the gate must boot and run to a verdict");
            var package = report.Packages.Single(p => p.Id == "Course");
            package.InstallError.Should().BeNull("the package's nodes must install");

            // 🚨 A MEASUREMENT, not merely an absent error. Before the fix these are 2 carried and
            // 0 served, with ContentError naming the refusal; a check that could only ever say
            // "nothing complained" would have stayed green through the whole of #3424.
            package.ContentAssets.Should().Be(2,
                "the package ships two content/** binaries — one owned by the Space root and one "
                + "by a child node");
            package.ContentError.Should().BeNull(
                "every committed binary must be published into the root's content collection AND "
                + "read back through the content route — the gate serves what a portal serves");
            package.ContentAssetsServed.Should().Be(2,
                "both assets must READ BACK, at the collection-relative paths the content route "
                + $"resolves them at ({string.Join(", ", ServedPaths)})");
            report.ExitCode.Should().Be(0, "nothing else about this package is broken");
        }
        finally
        {
            Cleanup(repo);
            Cleanup(bakeOut);
        }
    }

    /// <summary>
    /// The negative control: a package shipping NO binaries reports zero carried and no error. This
    /// is what keeps the check from being satisfiable by inertia — a run in which the content check
    /// never looked at anything would report the same "no error" as a run in which it verified two
    /// files, and only the COUNT tells the two apart.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task APackageWithNoBinaries_ReportsZeroCarried_AndNoContentFailure()
    {
        var repo = TempDirectory("mw-3424-nocontent");
        var bakeOut = TempDirectory("mw-3424-nocontent-bake");
        try
        {
            Write(repo, "Course/index.json", CourseIndexJson);
            Write(repo, "Course/Lesson1.json", LessonJson);

            var log = new StringWriter();
            var report = await PluginGateRunner.Run(new GateOptions
            {
                RepoRoot = repo,
                Output = log,
                BakeOutputDirectory = bakeOut,
                SourceSha = "cafebabe",
                CompileTimeout = TimeSpan.FromMinutes(4),
                RenderTimeout = TimeSpan.FromMinutes(2),
            }).FirstAsync().Await();
            output.WriteLine(log.ToString());

            report.FatalError.Should().BeNull();
            var package = report.Packages.Single(p => p.Id == "Course");
            package.InstallError.Should().BeNull();
            package.ContentAssets.Should().Be(0, "this package ships no content/** files at all");
            package.ContentAssetsServed.Should().Be(0);
            package.ContentError.Should().BeNull(
                "a package that ships no binaries must never fail the content check");
            report.ExitCode.Should().Be(0);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(bakeOut);
        }
    }

    private sealed class StringWriterAdapter(ITestOutputHelper output) : StringWriter
    {
        public override void WriteLine(string? value) => output.WriteLine(value ?? string.Empty);
    }

    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void Write(string root, string relative, string content)
    {
        var full = FullPath(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteBytes(string root, string relative, byte[] bytes)
    {
        var full = FullPath(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static string FullPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { /* best effort */ }
    }
}
