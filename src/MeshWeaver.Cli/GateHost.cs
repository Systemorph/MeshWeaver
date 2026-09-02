using System.Reflection;

namespace MeshWeaver.Cli;

/// <summary>
/// The <b>two-image contract</b> on the CLI path: the TESTER image EXECUTES, the PLATFORM (portal)
/// image SUPPLIES the reference set, the framework identity and the runtime — and both stages run
/// from a COMPOSED HOST, the portal's <c>/app</c> with the tester CLI laid beside it.
///
/// <para>🚨 <b>Why this exists (#3113).</b> Every reusable lane moved to this shape in #3022/#3071;
/// <c>memex build plugin</c> did not, and kept running <c>--entrypoint /app/mw-plugin-test</c>
/// straight from the tester image. The tester's <c>/app</c> is a strict SUBSET of the portal's
/// (measured on 3.0.0-rc9.ci.7534: 88 vs 219 assemblies — <c>MeshWeaver.Maps</c>, <c>.AI</c>,
/// <c>.ContentCollections.Indexing</c>, the Blazor and hosting halves exist only in the portal), so
/// content binding a portal-shipped assembly cannot compile. On MeshWeaver.Manufacturing#48 that
/// read as <c>CS0234 The type or namespace name 'Maps' does not exist in the namespace
/// 'MeshWeaver'</c> against <c>AppleMaps/Gallery</c> and <c>Cornerstone/Pricing</c> — a CONTENT-shaped
/// failure with an INFRASTRUCTURE cause, on source nobody had changed.</para>
///
/// <para>🚨 <b>The composition rules are NOT reimplemented here.</b> They live in exactly one place —
/// <c>.github/scripts/compose-gate-host.sh</c>, which the lanes fetch from the platform at their
/// pinned ref and this assembly EMBEDS byte-for-byte (<c>&lt;EmbeddedResource&gt;</c> on the very
/// same file; <c>GateHostCompositionTest</c> pins the equality byte-for-byte and drives the
/// script's own <c>--self-test</c> through the bytes this CLI puts on disk). Its ordering and its
/// fail-closed refusals were derived from measured incidents, and a second copy of them here would
/// be the drift trap this repository has paid for repeatedly. The CLI extracts that script and runs
/// it; it never composes a host itself.</para>
///
/// <para>The identity PRECONDITION is deliberately outside the script (which is pure file logic),
/// exactly as in the lanes: the tester's own <c>framework-identity</c> verb must report that the
/// tester and the portal resolve ONE identity before the composition is trusted.</para>
/// </summary>
public static class GateHost
{
    /// <summary>
    /// The composition script, embedded from <c>.github/scripts/compose-gate-host.sh</c> — the SAME
    /// file the reusable lanes fetch. One implementation, not a copy.
    /// </summary>
    public const string ComposeScriptName = "compose-gate-host.sh";

    /// <summary>The tester CLI the composed host is started with.</summary>
    public const string TesterCli = "mw-plugin-test.dll";

    /// <summary>
    /// The file whose presence identifies a PORTAL <c>/app</c>. A host without one resolves the
    /// fallback framework identity — which no bake may be published under — so its absence is a
    /// refusal, never a degraded pass.
    /// </summary>
    public const string SurfaceManifest = "meshweaver-surface.manifest";

    /// <summary>
    /// Where the composed host is mounted inside the container. The tester CLI is started from here
    /// (<c>dotnet /host/mw-plugin-test.dll</c>) while <c>--app /app</c> keeps the reference set and
    /// the identity the PORTAL image's own <c>/app</c>.
    /// </summary>
    public const string HostMount = "/host";

    /// <summary>
    /// The platform host's IMPLEMENTATION frameworks, passed to <c>compile</c> as
    /// <c>--shared-frameworks</c> so the reference set is what the portal's runtime compile sees.
    /// </summary>
    public const string SharedFrameworks = "/usr/share/dotnet/shared";

    /// <summary>
    /// 🚨 The one wrong value nothing else can refuse for us: the TESTER passed as the platform,
    /// which would silently restore the very reference-set gap the platform image closes.
    /// </summary>
    /// <param name="image">The image reference given as <c>--platform-image</c>.</param>
    /// <returns>True when the reference names the tester image.</returns>
    public static bool NamesTheTesterImage(string image) =>
        image.Contains("mw-plugin-test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the embedded <see cref="ComposeScriptName"/> into <paramref name="directory"/> and
    /// returns its path.
    /// </summary>
    /// <param name="directory">Directory to write the script into; created if absent.</param>
    /// <returns>The full path of the extracted script.</returns>
    /// <exception cref="InvalidOperationException">
    /// The resource is not in this assembly — i.e. the <c>&lt;EmbeddedResource&gt;</c> link to
    /// <c>.github/scripts/compose-gate-host.sh</c> was dropped from the .csproj. Failing here is the
    /// point: a CLI that silently composed nothing would run the gate against an empty host.
    /// </exception>
    public static string ExtractComposeScript(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ComposeScriptName);
        File.WriteAllBytes(path, ComposeScriptBytes());
        // 🚨 `chmod +x`, the same line every lane runs after fetching this script — and not
        // cosmetic even though the CLI starts it as `bash <script>`: the script re-invokes ITSELF
        // by path (`"$self" …`) to prove each of its rules, so without the bit its own --self-test
        // dies on "Permission denied" having verified nothing. A file written 0644 is exactly the
        // shape in which a proof stops being a proof.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    /// <summary>
    /// Removes a temporary tree the gate host was built from or in, REPORTING a failure rather than
    /// swallowing it.
    ///
    /// <para>Two <c>/app</c> extractions plus the composed host are roughly a gigabyte per
    /// invocation, and the temp path carries the process id — so a later run never reuses it and
    /// stale copies accumulate until the disk is full.</para>
    ///
    /// <para>🚨 The narrow catch is deliberate and hides no fault: a directory that cannot be removed
    /// must not decide the build's verdict. A green gate turned red by a failed <c>rmdir</c>, or a
    /// red one whose real cause is buried under an IO exception raised on the cleanup path, are both
    /// worse than a note naming what was left behind — and a SILENT swallow would be worse than
    /// either, which is why this writes one.</para>
    /// </summary>
    /// <param name="directory">The tree to remove; a path that does not exist is a no-op.</param>
    /// <param name="notes">Where a failure to remove it is reported.</param>
    /// <returns>A task that completes when the tree is gone or the note is written.</returns>
    public static async Task DiscardTree(string directory, TextWriter notes)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await notes.WriteLineAsync(
                $"note: could not remove the temporary directory '{directory}' ({ex.Message}). It "
                + "holds a copy of an image's /app or the composed gate host — remove it by hand.");
        }
    }

    /// <summary>
    /// The embedded composition script's bytes — the same bytes as
    /// <c>.github/scripts/compose-gate-host.sh</c> in this repository.
    /// </summary>
    /// <returns>The script's content.</returns>
    /// <exception cref="InvalidOperationException">The resource is absent from this assembly.</exception>
    public static byte[] ComposeScriptBytes()
    {
        var assembly = typeof(GateHost).Assembly;
        using var stream = assembly.GetManifestResourceStream(ComposeScriptName)
            ?? throw new InvalidOperationException(
                $"'{ComposeScriptName}' is not embedded in {assembly.GetName().Name}. It is linked "
                + "from .github/scripts/compose-gate-host.sh by MeshWeaver.Cli.csproj so the CLI and "
                + "the reusable lanes compose the gate host by ONE set of rules; without it there is "
                + "no composition and no portal reference set. Restore the <EmbeddedResource>. "
                + $"Resources present: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
