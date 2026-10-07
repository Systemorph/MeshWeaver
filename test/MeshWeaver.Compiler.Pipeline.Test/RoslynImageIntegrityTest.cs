using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Leg 6 of the #890 / #5212 canary — the IMAGE leg (<see cref="RoslynImageIntegrity"/>).
///
/// <para>Leg 5 proved the fault travels with this process's one copy of Roslyn and left its
/// image, its mapping and its native code open. The doc then said "only the native code can get
/// worse while a process runs". <see cref="AnAssemblyRewrittenInPlace_IsReadFromTheNewBytes_InTheSameProcess"/>
/// is the deterministic repro that refutes that: a loaded assembly whose file is rewritten in place
/// answers from the NEW bytes inside the same process — so the mapping can change mid-process, and
/// the leg exists to say, at the failure, whether it did. The same test is the leg's negative
/// control: a reading that could not see that rewrite would report INTACT on every occurrence and
/// send the search to the JIT on evidence it never checked.</para>
/// </summary>
public class RoslynImageIntegrityTest(ITestOutputHelper output)
{
    private const string AssemblyName = "MwImageIntegrityProbe";

    /// <summary>
    /// Same source shape, one IL constant different, deterministic — so both images have the SAME
    /// layout (the bytes the runtime has already parsed stay in bounds) and differ only in the IL
    /// body and the content-derived MVID. A shorter or re-laid-out image would make the runtime
    /// read past what it mapped, which is a crash rather than a reading.
    /// </summary>
    private static byte[] Image(int value)
    {
        var source = $$"""
            namespace MwImageIntegrityProbe
            {
                public static class Probe
                {
                    public static int Value() => {{value}};
                }
            }
            """;
        var compilation = CSharpCompilation.Create(
            AssemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Success.Should().BeTrue(string.Join("; ", result.Diagnostics));
        return stream.ToArray();
    }

    private static bool InProbe(Type type) => type.Namespace == AssemblyName;

    [Fact]
    public void OnAHealthyProcess_TheCompilerImageReadsIntact()
    {
        RoslynImageIntegrity.EnsureBaseline();
        var reading = RoslynImageIntegrity.Reading();
        output.WriteLine(reading);
        output.WriteLine(RoslynImageIntegrity.Host());

        reading.Should().StartWith(RoslynImageIntegrity.Prefix + "INTACT",
            "nothing in this process rewrites the compiler — any other reading means the leg cannot "
            + "tell a healthy image from a broken one, and would answer nothing on an occurrence");
        reading.Should().Contain("Microsoft.CodeAnalysis.CSharp: unchanged")
            .And.Contain("Microsoft.CodeAnalysis: unchanged");
        RoslynImageIntegrity.Host().Should().StartWith("host=(").And.Contain("cores=");
    }

    [Fact]
    public void OnAHealthyProcess_TheLoadCanaryLoads()
    {
        // The shape-b control: the only acceptable reading on a healthy process is LOADS — a
        // control that cannot load a known-good image would mark every real load failure as a
        // process fault.
        EmitPipeline.RunLoadCanary().Should().StartWith("loadcanary=LOADS(3 types)");
        var verdict = EmitPipeline.ProbeEmittedImageLoads();
        output.WriteLine(verdict);
        verdict.Should().StartWith("loadcanary=LOADS(3 types)")
            .And.Contain(RoslynImageIntegrity.Prefix)
            .And.Contain("host=(");
    }

    [Fact]
    public void AnAssemblyRewrittenInPlace_IsReadFromTheNewBytes_InTheSameProcess()
    {
        var directory = Directory.CreateTempSubdirectory("mw-image-integrity-");
        var path = Path.Combine(directory.FullName, AssemblyName + ".dll");
        var context = new AssemblyLoadContext("image-integrity", isCollectible: true);
        try
        {
            var first = Image(1);
            var second = Image(2);
            second.Length.Should().Be(first.Length, "the two builds must share a layout");
            File.WriteAllBytes(path, first);

            var assembly = context.LoadFromAssemblyPath(path);
            var before = RoslynImageIntegrity.Read([assembly], InProbe);
            before.Images.Single().Unavailable.Should().BeNull();
            before.Images.Single().MappedMetadataMatchesDisk.Should().BeTrue();

            RoslynImageIntegrity.Classify(before, RoslynImageIntegrity.Read([assembly], InProbe))
                .Should().StartWith(RoslynImageIntegrity.Prefix + "INTACT", "nothing has changed yet");

            if (OperatingSystem.IsWindows())
            {
                // Windows locks a loaded image: the write that poisons a Unix process is refused
                // here, which is the platform half of the same fact.
                Action write = () => File.WriteAllBytes(path, second);
                write.Should().Throw<IOException>();
                return;
            }

            // IN PLACE: truncate + write the same inode — what File.WriteAllBytes and
            // File.Copy(overwrite: true) do to an existing path.
            File.WriteAllBytes(path, second);

            var after = RoslynImageIntegrity.Read([assembly], InProbe);
            var verdict = RoslynImageIntegrity.Classify(before, after);
            output.WriteLine(verdict);
            verdict.Should().StartWith(RoslynImageIntegrity.Prefix + "REWRITTEN-IN-PLACE",
                "the runtime reads the loaded image through a live file mapping, so an in-place "
                + "rewrite reaches the process — the metadata it executes from is now the new file's");
            after.Images.Single().MappedMetadataMatchesDisk.Should().BeTrue(
                "the mapping now reads the NEW file: that is the whole hazard");
            after.Images.Single().MappedMetadataSha256.Should().NotBe(before.Images.Single().MappedMetadataSha256);
        }
        finally
        {
            context.Unload();
            try { directory.Delete(recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void AFileReplacedByRename_LeavesTheMappingOnTheOriginal()
    {
        if (OperatingSystem.IsWindows())
            return; // a loaded image cannot be replaced over on Windows either

        var directory = Directory.CreateTempSubdirectory("mw-image-integrity-");
        var path = Path.Combine(directory.FullName, AssemblyName + ".dll");
        var context = new AssemblyLoadContext("image-integrity-rename", isCollectible: true);
        try
        {
            File.WriteAllBytes(path, Image(1));
            var assembly = context.LoadFromAssemblyPath(path);
            var before = RoslynImageIntegrity.Read([assembly], InProbe);

            // A RENAME over the path: a new inode; the old one stays mapped.
            var staged = Path.Combine(directory.FullName, "staged.dll");
            File.WriteAllBytes(staged, Image(2));
            File.Move(staged, path, overwrite: true);

            var verdict = RoslynImageIntegrity.Classify(before, RoslynImageIntegrity.Read([assembly], InProbe));
            output.WriteLine(verdict);
            verdict.Should().StartWith(RoslynImageIntegrity.Prefix + "FILE-REPLACED",
                "a rename never touches the mapped inode, so the process still reads the original — "
                + "the leg must not blame an image that the process is not executing");
        }
        finally
        {
            context.Unload();
            try { directory.Delete(recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void Classify_NamesEveryOutcome_AndAMissingBaselineAsMissing()
    {
        static RoslynImageIntegrity.ImageFingerprint Image(string disk, string mapped, string il) =>
            new("Microsoft.CodeAnalysis.CSharp", disk, mapped, true, il, 100, null);
        static RoslynImageIntegrity.ImageReading Reading(RoslynImageIntegrity.ImageFingerprint image) =>
            new([image]);

        var first = Reading(Image("d1", "m1", "i1"));
        RoslynImageIntegrity.Classify(first, Reading(Image("d1", "m1", "i1"))).Should().StartWith("image=INTACT");
        RoslynImageIntegrity.Classify(first, Reading(Image("d2", "m2", "i1"))).Should().StartWith("image=REWRITTEN-IN-PLACE");
        RoslynImageIntegrity.Classify(first, Reading(Image("d1", "m1", "i2"))).Should().StartWith("image=MAPPED-CHANGED",
            "IL read through the runtime changing while the file did not is in-memory corruption");
        RoslynImageIntegrity.Classify(first, Reading(Image("d2", "m1", "i1"))).Should().StartWith("image=FILE-REPLACED");
        RoslynImageIntegrity.Classify(null, Reading(Image("d1", "m1", "i1"))).Should().StartWith("image=NO-BASELINE(",
            "a reading with nothing to compare to must say so rather than read as intact");
        RoslynImageIntegrity.Classify(first, Reading(RoslynImageIntegrity.ImageFingerprint.Missing("x", "why")))
            .Should().StartWith("image=UNAVAILABLE", "a leg that could not read must never become a verdict");
        RoslynImageIntegrity.Classify(
                Reading(RoslynImageIntegrity.ImageFingerprint.Missing("Microsoft.CodeAnalysis.CSharp", "transient read failure")),
                Reading(Image("d1", "m1", "i1")))
            .Should().StartWith("image=NOT-COMPARED(",
                "a baseline that could not be read compares nothing, and INTACT must rest on a comparison");
        RoslynImageIntegrity.Classify(
                new([RoslynImageIntegrity.ImageFingerprint.Missing("Microsoft.CodeAnalysis", "transient"), Image("d1", "m1", "i1")]),
                new([new("Microsoft.CodeAnalysis", "d9", "m9", true, "i9", 100, null), Image("d1", "m1", "i1")]))
            .Should().StartWith("image=INTACT(",
                "one image compared is a comparison; the other is listed as not compared");
    }
}
