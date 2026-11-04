using System;
using System.IO;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Utils;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The record-read diagnostic names what a refusal CAN mean on the platform that produced it
/// (Copilot's review of #7873). Unix opens with <c>open(2)</c> and no advisory lock, so no other
/// holder can refuse the read and Linux's 11 (EAGAIN) is the SMB transport. Windows opens with
/// <c>File.OpenHandle</c>, where a handle held elsewhere without sharing still refuses the open, so
/// the Windows wording must name a sharing violation and must NOT claim that no holder can refuse
/// it. Both wordings are pinned on every host through the platform seam
/// (<c>RecordReadFault(fault, windows)</c>), and the production overload is pinned to the host.
/// </summary>
public class RecordReadFaultWordingTest
{
    private const string Record = "/data/modules/activation.d/Acme.Widgets/0123.json";

    private static LockFreeReadException OpenFault() =>
        new(LockFreeReadStep.Open, Record, new IOException("refused", 11));

    [Fact]
    public void OnUnix_TheOpenIsOpen2_WithNoAdvisoryLock_AndElevenIsTheTransport()
    {
        var message = ModuleActivationSidecar.RecordReadFault(OpenFault(), windows: false).Message;

        Assert.Contains("step: OPEN (open(2) only — no advisory lock is taken)", message);
        Assert.Contains("on Linux, 11 (EAGAIN) at either step is the SMB transport", message);
        Assert.DoesNotContain("sharing violation", message);
        Assert.Contains(Record, message);
        Assert.Contains("error number (HResult): 11 ", message);
    }

    [Fact]
    public void OnWindows_TheOpenIsFileOpenHandle_AndASharingViolationIsNamed_NeverRuledOut()
    {
        var message = ModuleActivationSidecar.RecordReadFault(OpenFault(), windows: true).Message;

        Assert.Contains("step: OPEN (File.OpenHandle with full sharing)", message);
        Assert.Contains("sharing violation", message);
        Assert.DoesNotContain("open(2)", message);
        Assert.DoesNotContain("no advisory lock", message);
        Assert.DoesNotContain("EAGAIN", message);
        Assert.DoesNotContain("neither step can be refused", message);
        Assert.Contains(Record, message);
    }

    [Fact]
    public void TheProductionOverload_UsesTheHostsPlatform()
    {
        var fault = OpenFault();

        Assert.Equal(
            ModuleActivationSidecar.RecordReadFault(fault, OperatingSystem.IsWindows()).Message,
            ModuleActivationSidecar.RecordReadFault(fault).Message);
    }
}
