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
/// (the control instance, <see cref="PublishConfigKey"/>) lands every generation INTO the tier
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

    private static string? processRoot;
    private static bool processPublishes;

    /// <summary>The process-wide shared root, set once at boot by <see cref="Configure"/>; null → no tier.</summary>
    public static string? Root => Volatile.Read(ref processRoot);

    /// <summary>Whether this process is the store's producer (<see cref="PublishConfigKey"/>).</summary>
    public static bool Publishes => Volatile.Read(ref processPublishes) && Root is not null;

    /// <summary>
    /// Reads <see cref="ConfigKey"/> and <see cref="PublishConfigKey"/> into the process-wide
    /// setting. Called once at boot, AFTER the <c>{rid}</c> expansion and BEFORE anything resolves a
    /// module directory. A root still carrying an unexpanded placeholder is refused (null): an older
    /// reader would otherwise look for a literal <c>{rid}</c> directory.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The root now in effect, or null.</returns>
    public static string? Configure(IConfiguration? configuration)
    {
        var root = Normalize(configuration?[ConfigKey]);
        Volatile.Write(ref processRoot, root);
        Volatile.Write(ref processPublishes,
            bool.TryParse(configuration?[PublishConfigKey], out var publishes) && publishes);
        return root;
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
    /// The SEALED shared copy of <paramref name="leaf"/>, or null — no tier, not a generation, or no
    /// seal. Any I/O fault reading the tier answers null: the tier is an optimisation, never a reason
    /// to fail a resolution.
    /// </summary>
    public static string? SealedDirectory(string? sharedRoot, string? leaf)
    {
        if (Normalize(sharedRoot) is not { } root || !IsGeneration(leaf))
            return null;
        try
        {
            var directory = Path.Combine(root, leaf!);
            return File.Exists(Path.Combine(directory, SealFileName)) ? directory : null;
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
    {
        var local = Path.Combine(baseDirectory, "modules", leaf);
        if (Directory.Exists(local))
            return local;
        return SealedDirectory(sharedRoot, leaf) ?? local;
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
    {
        var root = Normalize(sharedRoot)
            ?? throw new ArgumentException("no shared module tier is configured", nameof(sharedRoot));
        if (!IsGeneration(leaf))
            throw new ArgumentException(
                $"'{leaf}' is not a content-addressed generation leaf — only generations are shared",
                nameof(leaf));
        var target = Path.Combine(root, leaf);
        if (File.Exists(Path.Combine(target, SealFileName)))
            return target;

        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, $".staging-{leaf}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (relativePath, bytes) in files)
            {
                var destination = Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }

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
                foreach (var staged in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                             .ToImmutableArray())
                {
                    var destination = Path.Combine(target, Path.GetRelativePath(staging, staged));
                    if (File.Exists(destination))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    MeshWeaver.Utils.NoReplaceMove.TryMove(staged, destination);
                }
            }

            // The seal, strictly last — and it lists what it seals, so a reader can check it.
            var listed = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(target, f).Replace('\\', '/'))
                .Where(f => !string.Equals(f, SealFileName, StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            MeshWeaver.Mesh.Persistence.AtomicFileWrite.PublishBytes(
                Path.Combine(target, SealFileName),
                System.Text.Encoding.UTF8.GetBytes(string.Join('\n', listed) + "\n"));
            return target;
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
