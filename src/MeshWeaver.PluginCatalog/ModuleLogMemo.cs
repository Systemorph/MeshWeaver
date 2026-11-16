using System.Collections.Concurrent;
using System.Text;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// One module's record log (<see cref="ModuleActivationSidecar.ModuleLog"/>) remembered for as long
/// as the module's record directory LISTS the same files, so a whole-volume activation read opens
/// only the record directories that changed since the last read.
///
/// <para>🚨 <b>Why this exists: a registry publish cost one whole-volume walk per module ON THE
/// VOLUME, twice.</b> Every <c>POST /api/plugins/bundles/{plugin}</c> runs
/// <see cref="ModuleLandingService.ShelveModule"/> and then
/// <see cref="ModuleLandingService.ProposeModuleSet"/>, and each derives the activation of EVERY
/// module from its records: one listing per record kind and one open/read/close per landing record
/// and per platform verdict, for every module the registry holds. Measured locally under strace
/// for a volume of 40 modules with five landings each: 1,403 file opens and 1,981 stats for ONE
/// publish of ONE module, about 2 opens per record file on the volume. On the registry's Azure
/// Files share, where reading one small record costs ~14 ms (687 set records in 9–10 s, see
/// <see cref="ModuleSetStore"/>), that is the ~23 s per publish the Plugins publication lane
/// measured, and it GROWS with every landing until the modules GC retires records at the next
/// boot — so a 42-bundle publication outgrew its job's 20-minute cap.</para>
///
/// <para><b>Why a listing is a sound key.</b> Every file in a record directory is written ONCE —
/// <c>WriteOnce</c> publishes a complete file by a no-replace rename and never rewrites a name —
/// and is only ever removed (the modules GC, an uninstall). A record's arrival is its file's write
/// time. So the set of (name, length, write time) the directory lists determines every byte the
/// log is derived from: a record landed by another replica, one retired by the GC, or one
/// re-created under the same name after a retirement each changes the listing. The key is taken
/// before AND after a read, and the log is kept only when the two agree and the read was whole,
/// so a file arriving or leaving during the read is never folded into a remembered log. A log
/// with faults is never kept (MeshWeaver#7690): the next read takes it again.</para>
///
/// <para>An INSTANCE owned by the landing service, so its lifetime is the mesh's (no static
/// state). Concurrent readers on the landing lane and the read pool may both miss and both read;
/// both store a correct log.</para>
/// </summary>
internal sealed class ModuleLogMemo
{
    private readonly ConcurrentDictionary<string, (string Listing, ModuleActivationSidecar.ModuleLog Log)> logs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many module logs were read off the volume rather than remembered — the
    /// measurement the tests that pin this cost read.</summary>
    public int VolumeReads => Volatile.Read(ref volumeReads);

    private int volumeReads;

    /// <summary>
    /// The module's record log: the remembered one when the record directory still lists the same
    /// files, otherwise read whole by <paramref name="read"/> and remembered when nothing moved
    /// during the read.
    /// </summary>
    /// <param name="baseDirectory">The deployment root.</param>
    /// <param name="moduleName">The module whose record directory is read.</param>
    /// <param name="read">The volume read — <see cref="ModuleActivationSidecar.ReadModuleLog"/>.</param>
    /// <param name="cancellationToken">The pool's token, observed before the listing.</param>
    public ModuleActivationSidecar.ModuleLog Read(
        string baseDirectory, string moduleName,
        Func<ModuleActivationSidecar.ModuleLog> read, CancellationToken cancellationToken)
    {
        var before = Listing(baseDirectory, moduleName, cancellationToken);
        if (before is not null && logs.TryGetValue(moduleName, out var held)
            && string.Equals(held.Listing, before, StringComparison.Ordinal))
            return held.Log;

        Interlocked.Increment(ref volumeReads);
        var log = read();
        if (!log.Whole || before is null)
        {
            logs.TryRemove(moduleName, out _);
            return log;
        }
        var after = Listing(baseDirectory, moduleName, cancellationToken);
        if (string.Equals(before, after, StringComparison.Ordinal))
            logs[moduleName] = (before, log);
        else
            logs.TryRemove(moduleName, out _);
        return log;
    }

    /// <summary>
    /// What the module's record directory lists: every file's name, length and write time, in
    /// ordinal order; an absent directory reads as its own marker. Null when the directory could
    /// not be listed — a key that matches nothing, so the read is taken and reports the fault.
    /// </summary>
    internal static string? Listing(string baseDirectory, string moduleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(ModuleActivationSidecar.EntriesDirectory(baseDirectory), moduleName);
        try
        {
            if (!Directory.Exists(directory))
                return "(absent)";
            var key = new StringBuilder();
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles()
                         .OrderBy(f => f.Name, StringComparer.Ordinal)
                         .ToArray())
                key.Append(file.Name).Append('|').Append(file.Length)
                    .Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
            return key.ToString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
