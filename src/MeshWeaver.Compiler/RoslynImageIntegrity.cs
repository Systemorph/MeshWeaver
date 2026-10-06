using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.Compiler;

/// <summary>
/// Leg 6 of the #890 / #5212 canary — the IMAGE leg. Leg 5 (<see cref="PrivateRoslynCopy"/>,
/// <c>compiler=PRIVATE-COPY-EMITS</c>) proved the fault travels with this process's ONE copy of
/// Roslyn, and left three candidates open: that copy's <b>image</b>, its <b>mapping</b>, or the
/// <b>native code</b> produced for it. This leg separates the first two from the third by READING
/// the image the runtime is executing from, now, and comparing it with what it read at the first
/// compile of the process and with the file on disk.
///
/// <para>🚨 <b>Why "only the native code can get worse while a process runs" was wrong.</b> The
/// runtime executes an IL-only assembly loaded from a path out of a <b>file mapping</b>. On
/// Linux and macOS that mapping is live: a write INTO the same file (truncate + write — what
/// <c>File.WriteAllBytes</c> and <c>File.Copy(…, overwrite: true)</c> do to an existing path, or
/// any writer through a hard link to the same inode) changes the metadata and IL bodies the
/// runtime reads for every method it has not yet compiled — and for every method it RE-compiles
/// at tier 1, inlinees included. Measured (<c>RoslynImageIntegrityTest</c>): an assembly loaded by
/// path, its file then rewritten in place with a different build, answers reflection from the NEW
/// bytes inside the same process. That is exactly the shape #890 has: acquired mid-process,
/// deepening as more code tiers up, and immune in a private copy loaded from fresh bytes. So the
/// mapping CAN change while a process runs, and only a reading taken at the failure can say
/// whether it did.</para>
///
/// <para><b>What it reads</b>, for <c>Microsoft.CodeAnalysis</c> and
/// <c>Microsoft.CodeAnalysis.CSharp</c>:
/// <list type="bullet">
///   <item>the MAPPED metadata block (<c>Assembly.TryGetRawMetadata</c> — the
///     runtime's own pointer into the image it executes), hashed and compared byte-for-byte with
///     the metadata block of the file on disk read fresh;</item>
///   <item>the IL bodies of every method on the guard path (<c>Microsoft.Cci</c>, the C# symbol and
///     emit namespaces), read through the runtime (<see cref="MethodBody.GetILAsByteArray"/>), so
///     from the same mapping the JIT reads;</item>
///   <item>the on-disk file: length, write time and SHA-256.</item>
/// </list></para>
///
/// <para><b>What the answers mean</b> (<see cref="Classify"/> is pure and owns them):
/// <list type="bullet">
///   <item><c>image=INTACT</c> — the image and its mapping read exactly as at the first compile and
///     match the file. With <c>compiler=PRIVATE-COPY-EMITS</c>, the only shared-copy state left is
///     the NATIVE CODE (what the JIT produced, or the code heap it lives in): the next measurement
///     is the split-arm <c>DOTNET_TieredPGO=0</c> run and the JIT listing, never the file.</item>
///   <item><c>image=REWRITTEN-IN-PLACE</c> — the file changed AND the mapped bytes changed with it:
///     something wrote into a loaded compiler image. Find the writer; no runtime report is
///     warranted.</item>
///   <item><c>image=MAPPED-CHANGED</c> — the mapped bytes no longer match the first reading but the
///     file is unchanged: the image's pages were corrupted in memory.</item>
///   <item><c>image=FILE-REPLACED</c> — the path names different bytes, but the mapping still reads
///     the original (a rename over the path; harmless to this process) — not the cause.</item>
///   <item><c>image=NO-BASELINE</c> — the first reading was never taken before the failure (a
///     caller that emits through Roslyn directly, never through <see cref="EmitPipeline"/>), so only
///     mapped-vs-disk is compared and the verdict says so.</item>
/// </list></para>
///
/// <para><b>Cost and reach.</b> The baseline is ONE reading per process, taken at the first
/// <see cref="EmitPipeline.EmitCompilationToDirectory(CSharpCompilation,string,string,string,CancellationToken)"/>
/// — two file hashes and a few thousand IL reads, a few tens of milliseconds — and the comparison
/// runs only on the already-failing canary path. It is process-lifetime state by nature (the
/// mapping belongs to the PROCESS, not to a mesh, and outlives every mesh a test host builds), so
/// it is one immutable record published once with <see cref="Interlocked.CompareExchange{T}"/>,
/// never a cache: nothing ever adds to it, replaces it or clears it.</para>
/// </summary>
internal static class RoslynImageIntegrity
{
    /// <summary>The verdict token every outcome of this leg starts with.</summary>
    internal const string Prefix = "image=";

    /// <summary>The first reading in this process; written once, never replaced.</summary>
    private static ImageReading? baseline;

    /// <summary>
    /// Takes the process's first reading if none was taken yet. Idempotent and cheap after the
    /// first call; never throws (a reading that cannot be taken is recorded as such).
    /// </summary>
    internal static void EnsureBaseline()
    {
        if (Volatile.Read(ref baseline) is not null)
            return;
        Interlocked.CompareExchange(ref baseline, Read(RoslynAssemblies()), null);
    }

    /// <summary>
    /// The <c>image=</c> token for this moment, compared with the process baseline. Never throws:
    /// it runs on an already-failing path, and a diagnostic that faults destroys the evidence.
    /// </summary>
    internal static string Reading()
    {
        try
        {
            var before = Volatile.Read(ref baseline);
            return Classify(before, Read(RoslynAssemblies()));
        }
        catch (Exception probeError)
        {
            return $"{Prefix}UNAVAILABLE({probeError.GetType().Name} — the probe itself faulted, "
                + "so it says nothing either way)";
        }
    }

    /// <summary>The two assemblies whose single shared copy every compile in the process runs.</summary>
    private static ImmutableArray<Assembly> RoslynAssemblies() =>
        [typeof(Compilation).Assembly, typeof(CSharpCompilation).Assembly];

    /// <summary>Reads the given assemblies now. Test seam: the control test reads its OWN
    /// assembly, loaded by path, so it can rewrite that file without touching Roslyn.</summary>
    /// <param name="assemblies">The loaded assemblies to read.</param>
    /// <param name="ilScope">Which types' IL bodies to hash; the #890 guard path by default.</param>
    internal static ImageReading Read(IReadOnlyList<Assembly> assemblies, Func<Type, bool>? ilScope = null) =>
        new([.. assemblies.Select(a => ReadOne(a, ilScope ?? OnGuardPath))]);

    /// <summary>
    /// Reduces a baseline and a current reading to one token. Pure — no Roslyn, no process state —
    /// so every branch, including the ones only a poisoned process could produce, is unit-testable.
    /// </summary>
    internal static string Classify(ImageReading? before, ImageReading now)
    {
        var unavailable = now.Images.Where(i => i.Unavailable is not null).ToArray();
        if (unavailable.Length == now.Images.Length || now.Images.IsEmpty)
            return $"{Prefix}UNAVAILABLE({string.Join("; ", unavailable.Select(i => $"{i.Name}: {i.Unavailable}"))})";

        if (before is null)
        {
            var mismatched = now.Images.Where(i => i.Unavailable is null && !i.MappedMetadataMatchesDisk).ToArray();
            return mismatched.Length == 0
                ? $"{Prefix}NO-BASELINE(mapped metadata matches the file on disk for "
                    + $"{string.Join(", ", now.Images.Where(i => i.Unavailable is null).Select(i => i.Name))}; "
                    + "no first-compile reading was taken in this process, so a change made BEFORE "
                    + "this moment that rewrote file and mapping alike cannot be seen)"
                : $"{Prefix}NO-BASELINE-MAPPED-DIFFERS({string.Join(", ", mismatched.Select(i => i.Name))}: "
                    + "the metadata the runtime executes from is NOT the metadata of the file at that path)";
        }

        var findings = new List<string>();
        var worst = Outcome.Intact;
        foreach (var current in now.Images)
        {
            var first = before.Images.FirstOrDefault(i => i.Name == current.Name);
            if (current.Unavailable is not null || first is null || first.Unavailable is not null)
            {
                findings.Add($"{current.Name}: not compared ({current.Unavailable ?? first?.Unavailable ?? "no baseline entry"})");
                continue;
            }

            var fileChanged = current.DiskSha256 != first.DiskSha256;
            var mappedChanged = current.MappedMetadataSha256 != first.MappedMetadataSha256
                || current.IlSha256 != first.IlSha256;
            var outcome = (fileChanged, mappedChanged) switch
            {
                (true, true) => Outcome.RewrittenInPlace,
                (false, true) => Outcome.MappedChanged,
                (true, false) => Outcome.FileReplaced,
                _ => Outcome.Intact,
            };
            if (outcome > worst)
                worst = outcome;
            findings.Add($"{current.Name}: {outcome switch
            {
                Outcome.RewrittenInPlace => "file AND mapped bytes changed since the first compile",
                Outcome.MappedChanged => "mapped bytes changed, file unchanged",
                Outcome.FileReplaced => "file changed, mapping still reads the original",
                _ => "unchanged",
            }} (metadata {Short(first.MappedMetadataSha256)}→{Short(current.MappedMetadataSha256)}, "
                + $"IL of {current.IlMethods} methods in scope {Short(first.IlSha256)}→{Short(current.IlSha256)}, "
                + $"file {Short(first.DiskSha256)}→{Short(current.DiskSha256)})");
        }

        var detail = string.Join("; ", findings);
        return worst switch
        {
            Outcome.RewrittenInPlace => $"{Prefix}REWRITTEN-IN-PLACE({detail}) — 🚨 something wrote INTO a "
                + "loaded compiler image: every method the runtime compiles from it now reads the new "
                + "bytes at the old layout. Find the writer (an in-place File.WriteAllBytes / "
                + "File.Copy(overwrite) on that path or a hard link to its inode); this is not a runtime bug",
            Outcome.MappedChanged => $"{Prefix}MAPPED-CHANGED({detail}) — 🚨 the image the runtime executes "
                + "from no longer reads as it did at the first compile, while the file is unchanged: its "
                + "pages were altered in memory",
            Outcome.FileReplaced => $"{Prefix}FILE-REPLACED({detail}) — the path now names different bytes "
                + "but the mapping still reads the original image, so the image is not the cause",
            _ => $"{Prefix}INTACT({detail}) — the compiler's image and its mapping read exactly as at the "
                + "first compile of this process. With compiler=PRIVATE-COPY-EMITS that leaves the NATIVE "
                + "CODE of the shared copy (what the JIT produced for it, or the code heap it lives in) "
                + "as the only shared-copy state that differs from the private copy",
        };
    }

    /// <summary>The severity order of the per-assembly outcomes; the line reports the worst.</summary>
    private enum Outcome { Intact, FileReplaced, MappedChanged, RewrittenInPlace }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;

    private static ImageFingerprint ReadOne(Assembly assembly, Func<Type, bool> ilScope)
    {
        var name = "(unnamed)";
        try
        {
            name = assembly.GetName().Name ?? name;
            var location = assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
                return ImageFingerprint.Missing(name, "no on-disk image (single-file host or loaded from bytes)");

            var disk = File.ReadAllBytes(location);
            var mapped = MappedMetadata(assembly);
            if (mapped is null)
                return ImageFingerprint.Missing(name, "the runtime exposes no raw metadata for it");

            using var pe = new PEReader(ImmutableArray.Create(disk));
            var diskMetadata = pe.GetMetadata().GetContent();
            return new ImageFingerprint(
                name,
                Convert.ToHexStringLower(SHA256.HashData(disk)),
                Convert.ToHexStringLower(SHA256.HashData(mapped)),
                mapped.AsSpan().SequenceEqual(diskMetadata.AsSpan()),
                ScopedIl(assembly, ilScope, out var methods),
                methods,
                Unavailable: null);
        }
        catch (Exception readError)
        {
            return ImageFingerprint.Missing(name, $"{readError.GetType().Name}: {readError.Message}");
        }
    }

    /// <summary>A COPY of the metadata block the runtime is executing from, read through the
    /// runtime's own pointer into the loaded image — not the file.</summary>
    private static unsafe byte[]? MappedMetadata(Assembly assembly)
    {
        if (!assembly.TryGetRawMetadata(out var blob, out var length) || blob is null || length <= 0)
            return null;
        return new ReadOnlySpan<byte>(blob, length).ToArray();
    }

    /// <summary>
    /// SHA-256 over the IL bodies of every method of the types in scope (the #890 guard path in
    /// production), read through the runtime
    /// (so from the same mapping the JIT reads) in metadata-token order so the hash is stable.
    /// </summary>
    private static string ScopedIl(Assembly assembly, Func<Type, bool> ilScope, out int methods)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            types = [.. partial.Types.Where(t => t is not null).Select(t => t!)];
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        methods = 0;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in types.Where(ilScope).OrderBy(t => t.MetadataToken))
        {
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all))
                         .OrderBy(m => m.MetadataToken))
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null)
                    continue;
                hash.AppendData(BitConverter.GetBytes(method.MetadataToken));
                hash.AppendData(il);
                methods++;
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool OnGuardPath(Type type) =>
        type.Namespace is { } ns
        && (ns == "Microsoft.Cci"
            || ns == "Microsoft.CodeAnalysis.CSharp.Symbols"
            || ns == "Microsoft.CodeAnalysis.CSharp.Emit");

    /// <summary>One reading of a set of loaded images.</summary>
    internal sealed record ImageReading(ImmutableArray<ImageFingerprint> Images);

    /// <summary>One loaded image, as read at one moment.</summary>
    /// <param name="Name">Simple assembly name.</param>
    /// <param name="DiskSha256">SHA-256 of the file at the assembly's location, read fresh.</param>
    /// <param name="MappedMetadataSha256">SHA-256 of the metadata block the runtime executes from.</param>
    /// <param name="MappedMetadataMatchesDisk">Whether that block equals the file's metadata block.</param>
    /// <param name="IlSha256">SHA-256 over the guard-path IL bodies read through the runtime.</param>
    /// <param name="IlMethods">How many method bodies <paramref name="IlSha256"/> covers.</param>
    /// <param name="Unavailable">Why the image could not be read, or <c>null</c>.</param>
    internal sealed record ImageFingerprint(
        string Name, string DiskSha256, string MappedMetadataSha256, bool MappedMetadataMatchesDisk,
        string IlSha256, int IlMethods, string? Unavailable)
    {
        /// <summary>An image that could not be read, with the reason.</summary>
        internal static ImageFingerprint Missing(string name, string why) =>
            new(name, "", "", false, "", 0, why);
    }

    /// <summary>
    /// The <c>host=</c> token: what the JIT was generating code FOR, and with which tiering knobs —
    /// the variables a native-code fault depends on and every earlier occurrence left unrecorded,
    /// so occurrences can be compared by CPU and instruction set rather than by guess. Never throws.
    /// </summary>
    internal static string Host()
    {
        try
        {
            var isa = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 =>
                    $"avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} "
                    + $"avx512f={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}",
                Architecture.Arm64 => $"advsimd={System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported}",
                _ => "isa=unknown",
            };
            var knobs = string.Join(" ", new[] { "TieredCompilation", "TieredPGO", "ReadyToRun", "TC_QuickJitForLoops", "OSR_HitLimit" }
                .Select(k => (k, v: Environment.GetEnvironmentVariable("DOTNET_" + k)))
                .Where(kv => kv.v is not null)
                .Select(kv => $"DOTNET_{kv.k}={kv.v}"));
            return $"host=({RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier} "
                + $"cpu='{CpuModel()}' cores={Environment.ProcessorCount} {isa}"
                + (knobs.Length == 0 ? " tiering=defaults" : " " + knobs) + ")";
        }
        catch (Exception probeError)
        {
            return $"host=UNAVAILABLE({probeError.GetType().Name})";
        }
    }

    private static string CpuModel()
    {
        const string cpuinfo = "/proc/cpuinfo";
        if (!File.Exists(cpuinfo))
            return "unknown";
        var line = File.ReadLines(cpuinfo)
            .FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
        return line?[(line.IndexOf(':') + 1)..].Trim() ?? "unknown";
    }
}
