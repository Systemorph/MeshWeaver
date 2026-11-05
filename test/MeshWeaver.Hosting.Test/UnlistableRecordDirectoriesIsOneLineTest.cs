using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>One listing fault is ONE line (MeshWeaver#7909, #7910).</b>
///
/// <para><b>The incident.</b> On one memex pod, listing the record directories under
/// <c>activation.d</c> failed once (<c>IOException: Host is down</c>; its cause was not established, and the next read listed
/// the directories whole).
/// The fail-closed rule then dropped every current-image projection from that read, which is the
/// designed answer — but it reported each drop as its own Error line. One transient fault became 43
/// Error lines within 4 ms, and the log watcher filed them as two incidents (the cause, and the 42
/// consequences) about one event.</para>
///
/// <para><b>What makes this able to fail.</b> The fault is real: the entry files are read first,
/// exactly as in production, and only then is <c>activation.d</c> made unlistable (its permissions
/// removed on Unix). The test first asserts that the listing genuinely faults, then that the read
/// reports it in exactly one line naming every dropped module, and that every projection is still
/// dropped — the fail-closed answer is unchanged. Unix only: on Windows a directory ACL is the
/// equivalent, and the drop path is platform-independent.</para>
/// </summary>
public class UnlistableRecordDirectoriesIsOneLineTest : IDisposable
{
    private static readonly ImmutableArray<string> Modules = ["Acme.Gadgets", "Acme.Widgets"];

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-unlistable-" + Guid.NewGuid().ToString("N"));

    public UnlistableRecordDirectoriesIsOneLineTest()
    {
        foreach (var module in Modules)
            ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry
            {
                Name = module,
                Directory = module + "@0123456789abcdef",
                Version = "1.0.0",
                ProjectionOf = "0001.json",
            });
    }

    public void Dispose()
    {
        var entries = ModuleActivationSidecar.EntriesDirectory(root);
        if (!OperatingSystem.IsWindows() && Directory.Exists(entries))
            File.SetUnixFileMode(entries,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    [Fact]
    public void AnUnlistableRecordDirectory_DropsEveryProjection_InOneLine()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the fault is injected through Unix permissions");
            return;
        }
        var stored = ModuleActivationSidecar.ReadStoredActivation(root, onCorrupt: null);
        Assert.Equal(Modules, stored.List.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));

        var faults = ImmutableList.CreateBuilder<string>();
        ModuleActivationList list;
        var entries = ModuleActivationSidecar.EntriesDirectory(root);
        var mode = File.GetUnixFileMode(entries);
        File.SetUnixFileMode(entries, UnixFileMode.None);
        try
        {
            // The precondition: the listing is genuinely refused, so a single line cannot come
            // from a listing that succeeded.
            Assert.NotNull(ModuleActivationSidecar.TryListRecordDirectories(root, default).Fault);
            list = ModuleActivationSidecar.ApplyLandingRecords(root, stored, faults.Add, () => null);
        }
        finally
        {
            File.SetUnixFileMode(entries, mode);
        }

        var line = Assert.Single(faults);
        Assert.StartsWith($"Module record directories under '{entries}' could not be listed", line);
        Assert.Contains("2 module(s) are NOT loaded from this read: [Acme.Gadgets, Acme.Widgets]", line);
        Assert.Empty(list.Entries);
    }

    /// <summary>The negative control: with the listing readable, nothing is reported and both
    /// projections stay — so the drop above is the fault's, not the fixture's.</summary>
    [Fact]
    public void AListableRecordDirectory_KeepsEveryProjection_AndReportsNothing()
    {
        var stored = ModuleActivationSidecar.ReadStoredActivation(root, onCorrupt: null);
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.ApplyLandingRecords(root, stored, faults.Add, () => null);

        Assert.Empty(faults);
        Assert.Equal(Modules, list.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
    }
}
