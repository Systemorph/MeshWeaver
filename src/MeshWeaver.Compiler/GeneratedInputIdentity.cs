using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace MeshWeaver.Compiler;

/// <summary>
/// 🚨 THE CONTENT KEY of a compiled NodeType (#1707 slice 4): a hash of the <b>fully generated
/// compilation input</b> — the exact text handed to Roslyn after skeleton generation, source
/// aggregation, <c>@@</c>-include expansion and the <c>#r</c> strip — together with everything
/// else that decides the emitted bytes given that text: the assembly name, the parse/compilation
/// option set, the Roslyn version, the source generators that run, and the reference SURFACE set.
///
/// <para><b>Why it exists.</b> Every invalidation signal that preceded it was a PROXY.
/// <see cref="FrameworkBuildIdentity.FullMvidAssemblies"/> folds the toolchain's full
/// implementation MVID into the framework identity precisely because a generator body change
/// reshapes what Roslyn is fed with no API change — so surface hashing cannot see it. That proxy
/// is correct and unbelievably coarse: a body-only commit to any member of the toolchain closure
/// (16 assemblies, 383 commits/30d — issue #1976) mints a new global identity, empties the
/// assembly share's key-space, and rebakes every NodeType on every deployment, including the ones
/// whose generated input is byte-for-byte what it was. This is the DIRECT observation the proxy
/// stands in for: two compiles whose generated input hashes equal produce interchangeable bytes,
/// whoever built them and whenever.</para>
///
/// <para><b>Two stages, because the two halves are known at different moments.</b>
/// <see cref="OfGeneratedInput"/> runs BEFORE Roslyn, where the generated text exists;
/// <see cref="Combine"/> folds in the PRUNED reference surfaces read off the emitted assembly
/// afterwards (Roslyn emits an AssemblyRef row only for what the produced code actually uses —
/// see <see cref="CompiledDependencies"/>). Hashing the pruned set rather than the candidate set
/// is what keeps the key per-type: a platform surface change invalidates the types that bind it,
/// not the world.</para>
///
/// <para><b>Determinism is the whole product.</b> An unstable key invalidates everything on every
/// build, which is the current complaint amplified. Everything enumerable is ordinal-sorted,
/// every value is rendered invariant-culture, and the source text is normalised by
/// <see cref="NormalizeGeneratedSource"/>. Exactly three normalisations are applied, and each is
/// a deliberate trade:
/// <list type="number">
/// <item><description><b>A leading UTF-8 BOM is dropped.</b> Whether the text carries one is a
/// property of how it was read, not of what it means.</description></item>
/// <item><description><b>Line endings are folded to <c>\n</c>.</b> The skeleton is built with
/// <c>StringBuilder.AppendLine</c>, i.e. <see cref="Environment.NewLine"/>, so the SAME node
/// generates CRLF text on a Windows dev box and LF in a Linux pod. Without this fold the key would
/// differ per host for identical content and nothing would ever share a build. The cost is
/// explicit and small: two inputs that differ ONLY in the line endings inside a verbatim/raw
/// string literal hash equal. Their emitted bytes do differ, so this is the one place the key is
/// deliberately coarser than the bytes — and it is coarser in the direction of a Windows-built
/// artifact matching its Linux twin, which is the shape a mixed-host mesh needs.</description></item>
/// <item><description><b>The skeleton's <c>// Generated at: &lt;UtcNow&gt;</c> header line is
/// replaced by a fixed marker</b> (<see cref="NormalizedGeneratedAtLine"/>).
/// <c>DynamicMeshNodeAttributeGenerator.GenerateAttributeSource</c> stamps a WALL CLOCK into the
/// generated text, so the input is never twice the same and no content key over it could ever
/// hit. It is a comment: it contributes no IL. (It does move the PDB's document hash, which is
/// why the assembly-store's digest-of-the-emitted-bytes can never dedupe two compiles of
/// identical content either — the observation that motivates this type.)</description></item>
/// <item><description><b>The skeleton's <c>LastModified = DateTimeOffset.Parse("…")</c> line is
/// replaced by a fixed marker</b> (<see cref="NormalizedLastModifiedLine"/>) — the SECOND wall
/// clock, and the one that is not obvious. The generator emits the NodeType node's own
/// <c>LastModified</c> into the provider's node, and <c>PackageInstaller.BulkSave</c> stamps that
/// field with <c>DateTimeOffset.UtcNow</c> on EVERY import. So the same repo content imported
/// twice generates different text: a key that discriminated on it would be unique per import,
/// never hit, and — decisively — a CI bake and a portal import the same commit at different
/// moments BY CONSTRUCTION, so a bundle could never match the input a portal would regenerate.
/// <para>🚨 This one is a real trade, unlike the other three: the value IS a string literal in the
/// emitted IL, so two assemblies differing only in it hash equal. What differs is one provenance
/// timestamp on the provider's node — not a type, a member, a signature or a reference — and the
/// alternative is a key that cannot work at all. The match is anchored to the generator's exact
/// emitted shape, so a same-shaped line in USER code is the only false positive available, and its
/// only consequence is that one timestamp literal stops discriminating.</para></description></item>
/// </list>
/// Nothing else is touched: no trimming, no whitespace collapsing, no comment stripping. Every
/// other byte of the generated text is part of the key.</para>
/// </summary>
public static class GeneratedInputIdentity
{
    /// <summary>Prefix of a stage-1 GENERATED-INPUT digest (<see cref="OfGeneratedInput"/>) — the
    /// text-and-toolchain half, before the reference surfaces are known.</summary>
    public const string GeneratedInputPrefix = "g";

    /// <summary>Prefix of the complete CONTENT KEY (<see cref="Combine"/>).</summary>
    public const string ContentKeyPrefix = "i";

    /// <summary>Recorded for an input whose identity cannot be resolved (a generator assembly that
    /// is not on disk, a reference with no id). Absence is part of the key, never silently
    /// skipped — two environments with different presence sets must not hash equal.</summary>
    public const string AbsentId = "absent";

    /// <summary>The fixed text every <c>// Generated at: …</c> line normalises to.</summary>
    internal const string NormalizedGeneratedAtLine = "// Generated at: (normalized)";

    /// <summary>The fixed text the skeleton's node-timestamp line normalises to.</summary>
    internal const string NormalizedLastModifiedLine =
        "                LastModified = DateTimeOffset.Parse(\"(normalized)\"),";

    /// <summary>
    /// Matches the skeleton's wall-clock header line. Anchored to a whole line (Multiline) and
    /// deliberately narrow — it must not touch a line of USER code that happens to mention the
    /// phrase inside a string literal, which is why it requires the line to START with the comment
    /// marker rather than merely contain it.
    /// </summary>
    private static readonly Regex GeneratedAtLinePattern = new(
        @"^//[ \t]*Generated at:.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Matches the skeleton's node-timestamp line. Anchored to a whole line and to the generator's
    /// exact emitted shape — the assignment, the <c>DateTimeOffset.Parse</c> call, a simple string
    /// literal, the trailing comma — so it cannot swallow an arbitrary line of user code that
    /// merely mentions <c>LastModified</c>.
    /// </summary>
    private static readonly Regex LastModifiedLinePattern = new(
        @"^[ \t]*LastModified = DateTimeOffset\.Parse\(""[^""\\]*""\),[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The identity of the Roslyn that will do the compiling. A compiler upgrade can change the
    /// emitted bytes for identical input, and it is NOT covered by anything else here: the
    /// framework identity walks <c>MeshWeaver.*</c> references only
    /// (<c>FrameworkBuildIdentity.ComputeToolchainClosure</c>), so <c>Microsoft.CodeAnalysis.*</c>
    /// has never been in it directly — it rode in on <c>MeshWeaver.Compiler</c>'s MVID, which a
    /// package bump moves. A content key that replaces that MVID must carry the compiler
    /// explicitly or it under-invalidates on exactly the change that rewrites every assembly.
    /// </summary>
    public static string CompilerIdentity { get; } = ResolveCompilerIdentity();

    private static string ResolveCompilerIdentity()
    {
        var assembly = typeof(CSharpCompilation).Assembly;
        var name = assembly.GetName();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.Create(CultureInfo.InvariantCulture,
            $"{name.Name}/{name.Version}/{informational ?? AbsentId}");
    }

    /// <summary>
    /// Normalises the fully generated compilation input for hashing — see the three normalisations
    /// documented on the type. Pure; a null/empty input normalises to the empty string.
    /// </summary>
    public static string NormalizeGeneratedSource(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return string.Empty;
        var text = source[0] == '\uFEFF' ? source[1..] : source;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        text = GeneratedAtLinePattern.Replace(text, NormalizedGeneratedAtLine);
        return LastModifiedLinePattern.Replace(text, NormalizedLastModifiedLine);
    }

    /// <summary>
    /// STAGE 1 — the digest of everything that is known BEFORE Roslyn runs: the generated text,
    /// the emitted assembly's name, the option set, the compiler, and the source generators that
    /// will run over the compilation.
    ///
    /// <para>The generators are in here rather than in the text because they ADD source Roslyn
    /// never shows the caller: a generator body change alters the emitted bytes while the text
    /// handed in is unchanged. Their identities are MVIDs
    /// (<see cref="AssemblyFileIdentities"/>) — a generator is a build tool, and the only honest
    /// question about one is "is it the same build".</para>
    /// </summary>
    /// <param name="assemblyName">The emitted assembly's name — it is written into the metadata,
    /// so it is part of the bytes.</param>
    /// <param name="generatedSource">The full C# text handed to Roslyn (post skeleton, aggregation,
    /// <c>@@</c>-include expansion and <c>#r</c> strip). Normalised here.</param>
    /// <param name="optionsFingerprint">The canonical rendering of the parse/compilation/emit
    /// options — see <see cref="OptionsFingerprint"/>.</param>
    /// <param name="compilerIdentity">The Roslyn identity — <see cref="CompilerIdentity"/> in
    /// production, injectable so the rule is testable without rebuilding the compiler.</param>
    /// <param name="generatorIdentities">Source-generator assembly file name → identity.</param>
    public static string OfGeneratedInput(
        string assemblyName,
        string? generatedSource,
        string optionsFingerprint,
        string compilerIdentity,
        IEnumerable<KeyValuePair<string, string>> generatorIdentities)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        ArgumentNullException.ThrowIfNull(optionsFingerprint);
        ArgumentNullException.ThrowIfNull(compilerIdentity);
        ArgumentNullException.ThrowIfNull(generatorIdentities);

        var document = new StringBuilder();
        document.Append("generated-input/v1\n");
        document.Append("assembly=").Append(assemblyName).Append('\n');
        document.Append("compiler=").Append(compilerIdentity).Append('\n');
        document.Append("options=").Append(Sha256Hex(optionsFingerprint)).Append('\n');
        AppendPairs(document, "generator", generatorIdentities);
        document.Append("source=")
            .Append(Sha256Hex(NormalizeGeneratedSource(generatedSource))).Append('\n');
        return GeneratedInputPrefix + Sha256Hex(document.ToString())[..32];
    }

    /// <summary>
    /// STAGE 2 — the complete CONTENT KEY: the stage-1 digest folded with the PRUNED reference
    /// surfaces the emitted assembly actually binds (<see cref="CompiledDependencies"/>'s
    /// assembly entries — platform assemblies by reference-assembly hash, modules and NuGet
    /// packages by MVID).
    ///
    /// <para>Reference SURFACES, not reference builds: two hosts whose platform assemblies present
    /// the same API surface resolve the same ids (#1696's construction), so the key is equal
    /// across them — which is the entire point of a content key that a bake and a portal can both
    /// compute.</para>
    /// </summary>
    /// <param name="generatedInputDigest">The stage-1 value from <see cref="OfGeneratedInput"/>.</param>
    /// <param name="referenceSurfaces">Referenced assembly simple name → surface id.</param>
    public static string Combine(
        string generatedInputDigest,
        IEnumerable<KeyValuePair<string, string>> referenceSurfaces)
    {
        ArgumentNullException.ThrowIfNull(generatedInputDigest);
        ArgumentNullException.ThrowIfNull(referenceSurfaces);

        var document = new StringBuilder();
        document.Append("content-key/v1\n");
        document.Append("input=").Append(generatedInputDigest).Append('\n');
        AppendPairs(document, "reference", referenceSurfaces);
        return ContentKeyPrefix + Sha256Hex(document.ToString())[..32];
    }

    /// <summary>
    /// Assembly file name → implementation MVID ("N" format), read METADATA-ONLY so nothing is
    /// loaded into the process. Keyed by FILE NAME rather than by full path: a path is a property
    /// of the host's layout, and a key that carried one could never match between a bake container
    /// and a portal pod. A missing or unreadable file resolves <see cref="AbsentId"/> — absence is
    /// part of the key.
    /// </summary>
    public static ImmutableSortedDictionary<string, string> AssemblyFileIdentities(
        IEnumerable<string> assemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var path in assemblyPaths)
        {
            if (string.IsNullOrEmpty(path))
                continue;
            builder[Path.GetFileName(path)] = ReadMvid(path) ?? AbsentId;
        }
        return builder.ToImmutable();
    }

    private static string? ReadMvid(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N");
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException
                                       or UnauthorizedAccessException)
        {
            // Unreadable is recorded as ABSENT, not swallowed: the caller's key then differs from
            // one computed where the file was readable, which is the conservative direction
            // (a rebuild), and the identity resolution must never fault a compile.
            return null;
        }
    }

    /// <summary>
    /// The canonical rendering of everything the toolchain tells Roslyn ABOUT the compilation,
    /// as opposed to the text of it. A body-only change to
    /// <c>EmitPipeline.CreateCompilationOptions</c> — flipping the optimization level, say —
    /// rewrites every emitted assembly while leaving the generated text identical, so the option
    /// set has to be in the key or the key under-invalidates on it.
    ///
    /// <para>🚨 REFLECTED, never hand-listed. A hand-list is how <c>NuGetDirectiveParser</c> sat
    /// outside the identity boundary while shaping compile input (#1707), and the same trap is
    /// waiting here: an option the toolchain starts setting would be silently outside the key, and
    /// the failure mode of that is stale bytes with no diagnostic. Reflecting over the option
    /// objects' public properties also makes the key sensitive to a Roslyn upgrade that ADDS an
    /// option, which is correct — the new default may change what is emitted.</para>
    ///
    /// <para>The PDB file path is deliberately NOT here: the emit sets it to a host-local
    /// directory, and a key carrying it could never match across hosts. It lands in the DLL's
    /// debug directory, so the emitted BYTES do differ by host — which is one more reason a digest
    /// of the emitted bytes cannot be a content key, and no reason to poison this one.</para>
    /// </summary>
    public static string OptionsFingerprint(
        CSharpParseOptions parseOptions,
        CSharpCompilationOptions compilationOptions,
        DebugInformationFormat debugInformationFormat)
    {
        ArgumentNullException.ThrowIfNull(parseOptions);
        ArgumentNullException.ThrowIfNull(compilationOptions);
        var text = new StringBuilder();
        text.Append("options/v1\n");
        text.Append(RenderOptions(parseOptions));
        text.Append(RenderOptions(compilationOptions));
        text.Append("emit.DebugInformationFormat=")
            .Append(debugInformationFormat.ToString()).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// One option object rendered as sorted <c>Type.Property=value</c> lines. Values are rendered
    /// invariant-culture; a collection is rendered as its ordinal-sorted elements so declaration
    /// order can never move the key; anything that is not a scalar, a string, an enum or a
    /// collection is rendered as its runtime TYPE NAME — never <c>ToString()</c> of an arbitrary
    /// object (which may embed a hash code) and never a reference identity.
    /// </summary>
    internal static string RenderOptions(object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var type = options.GetType();
        var text = new StringBuilder();
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);
        foreach (var property in properties)
            text.Append(type.Name).Append('.').Append(property.Name).Append('=')
                .Append(ReadAndRender(property, options)).Append('\n');
        return text.ToString();
    }

    private static string ReadAndRender(PropertyInfo property, object instance)
    {
        try
        {
            return RenderValue(property.GetValue(instance));
        }
        catch (Exception ex)
        {
            // A getter that throws is rendered as a STABLE marker naming the fault type, so the
            // fingerprint stays deterministic instead of faulting the compile that needs it.
            // It is not a swallow: the marker differs from every real value, so a property that
            // starts throwing invalidates — the safe direction.
            return "<threw:" + ex.GetType().Name + ">";
        }
    }

    private static string RenderValue(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case Enum e:
                return e.ToString();
            case IFormattable formattable when value.GetType().IsPrimitive
                                               || value is decimal or Guid:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case IDictionary dictionary:
                return Join(dictionary.Cast<DictionaryEntry>()
                    .Select(entry => RenderValue(entry.Key) + "=" + RenderValue(entry.Value)));
            case IEnumerable enumerable:
                return Join(enumerable.Cast<object?>().Select(RenderValue));
        }

        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            return RenderValue(type.GetProperty("Key")?.GetValue(value))
                   + "=" + RenderValue(type.GetProperty("Value")?.GetValue(value));
        return "<" + (type.FullName ?? type.Name) + ">";
    }

    private static string Join(IEnumerable<string> rendered) =>
        "[" + string.Join(",", rendered.OrderBy(s => s, StringComparer.Ordinal)) + "]";

    private static void AppendPairs(
        StringBuilder document, string label, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        foreach (var pair in pairs.OrderBy(p => p.Key, StringComparer.Ordinal))
            document.Append(label).Append('=').Append(pair.Key).Append('=')
                .Append(pair.Value ?? AbsentId).Append('\n');
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
