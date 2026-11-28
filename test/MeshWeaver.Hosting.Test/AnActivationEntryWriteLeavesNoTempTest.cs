using System;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A refused activation-entry write leaves no temp behind, and a transient refusal is re-tried.</b>
/// On the registry's shared Azure Files volume an overwrite-rename of <c>activation.d/&lt;Name&gt;.json</c>
/// was refused ("Access to the path ... is denied", HTTP 500 to the publisher, Plugins main run
/// 46124004180) and its <c>.tmp</c> stayed on the share. A directory standing at the target is a
/// refusal every OS reports as an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
/// that outlasts the re-tries, which is the shape the leak needs.
/// </summary>
public class AnActivationEntryWriteLeavesNoTempTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-entry-temp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static ModuleActivationEntry Entry(string name) => new()
    {
        Name = name,
        Directory = name + "@0123456789abcdef",
        Version = "1.0.0",
        ProjectionOf = "0001.json",
    };

    [Fact]
    public void A_write_that_cannot_land_throws_and_removes_its_temp()
    {
        Directory.CreateDirectory(ModuleActivationSidecar.EntryPath(root, "Acme.Gadgets"));
        Assert.ThrowsAny<Exception>(() => ModuleActivationSidecar.WriteEntry(root, Entry("Acme.Gadgets")));

        var leaked = Directory.GetFiles(ModuleActivationSidecar.EntriesDirectory(root), "*.tmp");
        Assert.Empty(leaked);
    }

    [Fact]
    public void A_write_that_can_land_does_and_leaves_no_temp()
    {
        ModuleActivationSidecar.WriteEntry(root, Entry("Acme.Widgets"));
        ModuleActivationSidecar.WriteEntry(root, Entry("Acme.Widgets"));

        Assert.True(File.Exists(ModuleActivationSidecar.EntryPath(root, "Acme.Widgets")));
        Assert.Empty(Directory.GetFiles(ModuleActivationSidecar.EntriesDirectory(root), "*.tmp"));
    }
}
