using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The READ-ONLY shared tier of the <c>modules/</c> tree — the environment's control-owned
/// artifact store (Doc/Architecture/SharedArtifactStore, increment 3; maintainer 2026-10-19:
/// <i>"shouldn't the modules cache be shared? must be built on build server and then shared. it
/// should be owned by control"</i>).
///
/// <para><b>Why a module generation may be shared and the rest of <c>modules/</c> may not.</b> A
/// landed generation's leaf is its CONTENT ADDRESS — <c>&lt;name&gt;@&lt;16 hex of SHA-256 over the
/// bytes&gt;</c> (#3656) — so two instances that land the same bundle compute the same leaf for the
/// same bytes, whichever instance and whichever platform build they run. Everything else under
/// <c>modules/</c> is per-instance STATE: the activation records, the verdicts measured against this
/// instance's platform, the module sets, the instance manifest, the legacy fixed
/// <c>modules/&lt;name&gt;/</c> folder (mutable, not addressed by content). None of that is ever
/// read from or written to this tier.</para>
///
/// <para><b>Resolution order — the writable tier first, then the shared tier.</b> A generation the
/// instance landed itself is local and wins; a generation it does not hold resolves to the shared
/// tier only when the shared copy is SEALED (<see cref="SealFileName"/> present, written strictly
/// last by the producer). An unsealed shared directory — a publication in flight or a torn one — is
/// never resolved, and the caller sees the ordinary local miss it would have seen without the tier.
/// So a portal whose store is unmounted, empty or mid-publication behaves exactly as it did before
/// the tier existed (policy <c>platform-delivery-must-be-tolerant</c>): nothing here can dead-page
/// a portal, it can only save a copy.</para>
///
/// <para><b>One producer.</b> A CONSUMER mounts the store read-only and never writes it. The OWNER
/// (the control instance, <see cref="PublishConfigKey"/>, set per module root by <see cref="Configure"/>) lands every generation INTO the tier
/// (<see cref="Publish"/>) and falls back to its own writable tier when that fails. The per-instance
/// modules GC (<see cref="ModuleLandingService.CollectGarbage(string, Microsoft.Extensions.Logging.ILogger?, TimeSpan?, DateTime?, CancellationToken)"/>)
/// walks only <c>&lt;root&gt;/modules</c>, so no portal ever deletes from the tier; the store is
/// swept by its owner's standing prune job.</para>
/// </summary>
public static class SharedModuleTier
{
    /// <summary>
    /// The shared tier's root — the directory holding the generation leaves
    /// (<c>&lt;store&gt;/{rid}/modules</c>, rendered by <c>DeploymentPortalConfig</c> for a record that
    /// takes part in the artifact store; <c>{rid}</c> is expanded at boot). Absent/blank → no tier.
    /// </summary>
    public const string ConfigKey = "Modules:SharedRoot";

    /// <summary>
    /// <c>true</c> on the store's OWNER: landings publish their generation into the shared tier.
    /// Anything else (absent, <c>false</c>, malformed) → the instance only reads the tier.
    /// </summary>
    public const string PublishConfigKey = "Modules:PublishToShared";

    /// <summary>The seal a producer writes strictly LAST into a published generation.</summary>
    public const string SealFileName = "_complete";

    /// <summary>One module root's tier: where its shared generations live and whether it publishes there.</summary>
    /// <param name="Root">The shared tier's root; null → no tier.</param>
    /// <param name="Publishes">Whether landings under this module root publish into <paramref name="Root"/>.</param>
    public sealed record Setting(string? Root, bool Publishes);

    /// <summary>
    /// 🚨 The tier is configured PER MODULE ROOT, never process-wide (review of MeshWeaver#7009). Two
    /// meshes in one process (a test host, a dev portal beside a run) each configure their own module
    /// root; a process-global setting let the second clear or redirect the first's resolver and its
    /// publication role. Keyed by the full module root, so every resolver and the landing service of ONE
    /// tree read the same setting, and another tree is untouched.
    /// </summary>
    private static ImmutableDictionary<string, Setting> settings =
        ImmutableDictionary<string, Setting>.Empty.WithComparers(StringComparer.Ordinal);

    private static string Key(string moduleRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(moduleRoot));

    /// <summary>The tier configured for <paramref name="moduleRoot"/>, or no tier.</summary>
    public static Setting For(string moduleRoot) =>
        settings.TryGetValue(Key(moduleRoot), out var setting) ? setting : new Setting(null, false);

    /// <summary>
    /// Reads <see cref="ConfigKey"/> and <see cref="PublishConfigKey"/> into the setting of
    /// <paramref name="moduleRoot"/>. Called at boot, AFTER the <c>{rid}</c> expansion and BEFORE anything
    /// resolves a module directory under that root. A root still carrying an unexpanded placeholder is
    /// refused (no tier): an older reader would otherwise look for a literal <c>{rid}</c> directory.
    /// </summary>
    /// <param name="moduleRoot">The module root this configuration governs (<see cref="ModuleRoot"/>).</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The setting now in effect for that root.</returns>
    public static Setting Configure(string moduleRoot, IConfiguration? configuration)
    {
        var root = Normalize(configuration?[ConfigKey]);
        var setting = new Setting(root,
            root is not null && bool.TryParse(configuration?[PublishConfigKey], out var publishes) && publishes);
        ImmutableInterlocked.Update(ref settings, s => s.SetItem(Key(moduleRoot), setting));
        return setting;
    }

    /// <summary>A configured root as the tier uses it: trimmed, null when blank or still templated.</summary>
    public static string? Normalize(string? configured) =>
        string.IsNullOrWhiteSpace(configured) || configured.Contains('{') || configured.Contains('}')
            ? null
            : configured.Trim();

    /// <summary>
    /// Whether <paramref name="leaf"/> is a content-addressed generation leaf
    /// (<c>&lt;name&gt;@&lt;id&gt;</c>, a bare name). Only those are ever resolved in, or published to,
    /// the shared tier — a legacy fixed folder is mutable and therefore per instance.
    /// </summary>
    public static bool IsGeneration(string? leaf) =>
        !string.IsNullOrWhiteSpace(leaf)
        && leaf.IndexOf('@') > 0
        && leaf.IndexOf('@') < leaf.Length - 1
        && leaf.IndexOfAny(['/', '\\']) < 0
        && leaf is not ("." or "..")
        && !leaf.StartsWith('.');

    /// <summary>
    /// The SEALED and WHOLE shared copy of <paramref name="leaf"/>, or null — no tier, not a generation,
    /// no seal, or a seal naming a file the directory no longer holds (an incomplete volume restore, a
    /// sweep that lost a race). A seal is a claim; it is honoured only while every file it lists is
    /// there. Any I/O fault reading the tier answers null: the tier is an optimisation, never a reason to
    /// fail a resolution.
    /// </summary>
    public static string? SealedDirectory(string? sharedRoot, string? leaf)
        => SealedDirectoryCore(sharedRoot, leaf, CancellationToken.None);

    /// <summary>
    /// <see cref="SealedDirectory"/> from a pooled leaf: <paramref name="cancellationToken"/> is checked
    /// before the seal is read and before each listed file is probed, one share round trip each, and a
    /// cancel propagates as <see cref="OperationCanceledException"/> rather than answering null.
    /// </summary>
    internal static string? SealedDirectoryObservingToken(
        string? sharedRoot, string? leaf, CancellationToken cancellationToken)
        => SealedDirectoryCore(sharedRoot, leaf, cancellationToken);

    private static string? SealedDirectoryCore(string? sharedRoot, string? leaf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Normalize(sharedRoot) is not { } root || !IsGeneration(leaf))
            return null;
        try
        {
            var directory = Path.Combine(root, leaf!);
            var seal = Path.Combine(directory, SealFileName);
            if (!File.Exists(seal))
                return null;
            foreach (var listed in File.ReadAllLines(seal).Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.IsPathRooted(listed) || listed.Split('/').Any(segment => segment == ".."))
                    return null;
                if (!File.Exists(Path.Combine(directory, listed.Replace('/', Path.DirectorySeparatorChar))))
                    return null;
            }
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 🚨 THE resolution rule: the instance's own <c>&lt;baseDirectory&gt;/modules/&lt;leaf&gt;</c> when
    /// it exists; else the sealed shared copy; else the local path (the ordinary miss). Pure apart from
    /// the existence probes.
    /// </summary>
    /// <param name="baseDirectory">The instance's writable module root.</param>
    /// <param name="sharedRoot">The shared tier's root; null → no tier.</param>
    /// <param name="leaf">The directory leaf (a generation, or a legacy module name).</param>
    public static string Resolve(string baseDirectory, string? sharedRoot, string leaf)
        => ResolveObservingToken(baseDirectory, sharedRoot, leaf, CancellationToken.None);

    /// <summary>
    /// <see cref="Resolve"/> from a pooled leaf: the same rule, with the shared copy's seal
    /// verification (<see cref="SealedDirectoryObservingToken"/>) checking
    /// <paramref name="cancellationToken"/> before each probe. A DISTINCT name, so the bare
    /// <c>Resolve</c> <c>cref</c>s stay unambiguous.
    /// </summary>
    internal static string ResolveObservingToken(
        string baseDirectory, string? sharedRoot, string leaf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var local = Path.Combine(baseDirectory, "modules", leaf);
        if (Directory.Exists(local))
            return local;
        return SealedDirectoryObservingToken(sharedRoot, leaf, cancellationToken) ?? local;
    }

    /// <summary>
    /// The owner's write: places the generation whose files are <paramref name="files"/> at
    /// <c>&lt;sharedRoot&gt;/&lt;leaf&gt;</c> and seals it LAST. Staged in a sibling directory and moved
    /// into place with one rename, so a reader never resolves a half-written generation; a target that
    /// already carries the seal is the answer (identical bytes by construction — the leaf is the content
    /// address), and an UNSEALED existing target (a publication that died before its seal) has its
    /// missing files restored before the seal is written. Never overwrites a file a reader may hold.
    /// </summary>
    /// <param name="sharedRoot">The shared tier's root.</param>
    /// <param name="leaf">The generation leaf.</param>
    /// <param name="files">Each file as (path relative to the generation, bytes).</param>
    /// <returns>The sealed directory.</returns>
    /// <exception cref="ArgumentException">No tier, or <paramref name="leaf"/> is not a generation.</exception>
    public static string Publish(
        string sharedRoot, string leaf, IReadOnlyList<(string RelativePath, byte[] Bytes)> files)
        => PublishWith(sharedRoot, leaf, files, MeshWeaver.Mesh.Persistence.AtomicFileWrite.OpenTempForWrite);

    /// <summary>
    /// <see cref="Publish"/> with the staging-file writer injected — the seam a test uses to stand the
    /// store on a volume that accepts a write and then does not keep it (a full share). Production never
    /// passes anything but <see cref="MeshWeaver.Mesh.Persistence.AtomicFileWrite.OpenTempForWrite"/>
    /// (the same seam <c>FileSystemAssemblyStore</c> exposes).
    /// </summary>
    /// <param name="sharedRoot">The shared tier's root.</param>
    /// <param name="leaf">The generation leaf.</param>
    /// <param name="files">Each file as (path relative to the generation, bytes).</param>
    /// <param name="openForWrite">Opens a staging path for writing.</param>
    /// <returns>The sealed directory.</returns>
    public static string PublishWith(
        string sharedRoot, string leaf, IReadOnlyList<(string RelativePath, byte[] Bytes)> files,
        Func<string, Stream> openForWrite)
        => PublishCore(sharedRoot, leaf, files, openForWrite, CancellationToken.None);

    /// <summary>
    /// <see cref="Publish"/> from a pooled leaf. <paramref name="cancellationToken"/> is checked before
    /// each staged file is written, before the rename that places the generation, before each file a
    /// pre-existing unsealed target has restored, and before the seal. Every one of those points leaves
    /// a state a later publication already repairs: a dot-prefixed staging directory the finally
    /// removes, or an UNSEALED target, which no reader resolves (<see cref="SealedDirectory"/>) and
    /// the next publication completes. A cancel never seals.
    /// </summary>
    internal static string PublishObservingToken(
        string sharedRoot, string leaf, IReadOnlyList<(string RelativePath, byte[] Bytes)> files,
        CancellationToken cancellationToken)
        => PublishCore(sharedRoot, leaf, files, MeshWeaver.Mesh.Persistence.AtomicFileWrite.OpenTempForWrite,
            cancellationToken);

    private static string PublishCore(
        string sharedRoot, string leaf, IReadOnlyList<(string RelativePath, byte[] Bytes)> files,
        Func<string, Stream> openForWrite, CancellationToken cancellationToken)
    {
        var root = Normalize(sharedRoot)
            ?? throw new ArgumentException("no shared module tier is configured", nameof(sharedRoot));
        if (!IsGeneration(leaf))
            throw new ArgumentException(
                $"'{leaf}' is not a content-addressed generation leaf — only generations are shared",
                nameof(leaf));
        var target = Path.Combine(root, leaf);
        if (SealedDirectoryCore(root, leaf, cancellationToken) is not null)
            return target;
        // A target that carries a seal but lacks a listed file (review of MeshWeaver#7009) falls
        // through: the files missing from it are restored from the bytes in hand below, and the seal —
        // already present — is left as it is.

        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, $".staging-{leaf}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (relativePath, bytes) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // 🚨 Flushed and length-checked, never File.WriteAllBytes (review of
                // MeshWeaver#7009): a full Azure Files share can accept a write and lose its bytes on
                // writeback without an exception here, and the seal below would then vouch for a
                // truncated module. ShortWriteException propagates, and the owner falls back to its
                // own volume.
                MeshWeaver.Mesh.Persistence.AtomicFileWrite.PublishBytesWith(destination, bytes, openForWrite);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var moved = false;
            if (!Directory.Exists(target))
            {
                try
                {
                    Directory.Move(staging, target);
                    moved = true;
                }
                catch (IOException) when (Directory.Exists(target))
                {
                    // Another producer moved the same content address in first.
                }
            }
            if (!moved)
            {
                // A snapshot (the loop moves files out of the tree it lists), collected with a step
                // boundary per listed path so a drain need not wait out the whole staging walk.
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var staged in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                             .Select(f => { cancellationToken.ThrowIfCancellationRequested(); return f; })
                             .ToImmutableArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(target, Path.GetRelativePath(staging, staged));
                    if (File.Exists(destination))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    MeshWeaver.Utils.NoReplaceMove.TryMove(staged, destination);
                }
            }

            // The seal, strictly last — and it lists what it seals (the files this publication carries),
            // so a reader checks it rather than trusting it. An existing seal is left as it is.
            cancellationToken.ThrowIfCancellationRequested();
            var listed = files
                .Select(f => f.RelativePath.Replace('\\', '/'))
                .Where(f => !string.Equals(f, SealFileName, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            MeshWeaver.Mesh.Persistence.AtomicFileWrite.PublishBytes(
                Path.Combine(target, SealFileName),
                System.Text.Encoding.UTF8.GetBytes(string.Join('\n', listed) + "\n"));
            // Past the seal the publication is committed; verifying it is not a step to cut.
            return SealedDirectoryCore(root, leaf, CancellationToken.None)
                ?? throw new IOException(
                    $"the shared generation {leaf} at {target} is not whole after publication — a listed file is missing");
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a staging leftover is unattributable to every reader (dot-prefixed).
            }
        }
    }
}
