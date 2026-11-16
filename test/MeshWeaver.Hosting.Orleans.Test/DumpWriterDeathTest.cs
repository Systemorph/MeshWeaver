using System;
using System.Diagnostics;
using System.IO;
using MeshWeaver.Connection.Orleans;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 The runtime reports a dump writer that died on a SIGNAL as a success (coreclr
/// <c>PROCCreateCrashDump</c>: <c>!WIFEXITED(status) || WEXITSTATUS(status) == 0</c>), so
/// <see cref="BlockedThreadStacks"/> must read "written" off the file, never off the call. Core #8355's CI
/// shard: <c>createdump</c> printed <c>Gathering state …</c> and nothing after it, <c>WriteDump</c> returned,
/// and the capture failed as a <see cref="FileNotFoundException"/> deep inside ClrMD. These tests inject the
/// fault for real — the dump writer is killed mid-dump — and require a named failure instead.
/// </summary>
public class DumpWriterDeathTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Fault injection: an outside process SIGKILLs this process's <c>createdump</c> child while it gathers
    /// state. The runtime's call returns normally (the precondition, pinned here so a runtime that starts
    /// reporting the death is noticed), and the capture fails NAMING the missing dump — not with ClrMD's
    /// <see cref="FileNotFoundException"/>, which is what it threw before.
    /// </summary>
    [Fact]
    public void ADumpWriterKilledMidDump_IsReportedAsWritten_AndTheCaptureNamesTheMissingDump()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the dump writer is a createdump child process on Unix only");
        var killedTheWriter = false;
        Exception? writeFailure = null;
        string? dumpPath = null;
        var stacks = new BlockedThreadStacks(writeDump: path =>
        {
            dumpPath = path;
            // Outside this process on purpose: createdump stops every thread of THIS process while it works.
            // Matched by the dump path on its command line (the runtime passes it as `--name <path>`), so the
            // killer can only ever hit THIS dump's writer — never one another test of this process started. The
            // `[c]` keeps the pattern from matching the killer's own command line, which is also our child. It
            // polls every 5 ms: far inside createdump's state-gathering window (about a second, measured), without
            // forking pkill back-to-back for that whole window.
            var killer = Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", $"while ! pkill -KILL -P {Environment.ProcessId} -f '[c]reatedump .*{path}'; do sleep 0.005; done" },
            }) ?? throw new InvalidOperationException("the killer shell did not start");
            try
            {
                BlockedThreadStacks.WriteDumpOfThisProcess(path);
            }
            catch (Exception ex)
            {
                writeFailure = ex;
            }
            finally
            {
                killedTheWriter = killer.WaitForExit(Budget) && killer.ExitCode == 0;
                if (!killer.HasExited)
                    killer.Kill();
                killer.Dispose();
            }
        });

        var failure = Record.Exception(() => stacks.CaptureThreadPoolWorkers());

        killedTheWriter.Should().BeTrue("the fault is injected only if createdump was killed while it ran");
        writeFailure.Should().BeNull("the runtime reports a dump writer killed by a signal as a success");
        failure.Should().BeOfType<InvalidOperationException>(
            "a dump that was never written is a named failure of the capture, never a FileNotFoundException from ClrMD; it was: {0}",
            failure?.ToString() ?? "no exception");
        failure!.Message.Should().Contain(dumpPath ?? "(the writer never ran)", "the failure names the dump that is missing");
        failure.Message.Should().Contain("signal", "the failure says why a 'written' dump can be absent");
        File.Exists(dumpPath).Should().BeFalse("no dump file is left behind");
    }

    /// <summary>
    /// A writer killed while WRITING leaves a file shorter than its own headers describe. The negative control
    /// reads the same real dump whole first: its headers describe a length the whole file covers, so the
    /// check passes a complete dump and only the cut one fails.
    /// </summary>
    [Fact]
    public void ADumpCutShortOfItsOwnHeaders_IsANamedFailure_AndAWholeOneIsNot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the headers read are the Unix dump writer's ELF / Mach-O cores");
        long whole = 0;
        long? described = null;
        string? dumpPath = null;
        var stacks = new BlockedThreadStacks(writeDump: path =>
        {
            dumpPath = path;
            BlockedThreadStacks.WriteDumpOfThisProcess(path);
            whole = new FileInfo(path).Length;
            described = BlockedThreadStacks.DescribedLength(path);
            BlockedThreadStacks.EnsureWritten(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
            file.SetLength(whole / 2);
        });

        var failure = Record.Exception(() => stacks.CaptureThreadPoolWorkers());

        described.Should().NotBeNull("the runtime's dump writer produces an ELF core on Linux and a Mach-O core on macOS");
        described!.Value.Should().BeLessThanOrEqualTo(whole, "a whole dump covers every byte its headers describe");
        described.Value.Should().BeGreaterThan(whole / 2, "a cut to half the file must fall short of what the headers describe");
        failure.Should().BeOfType<InvalidOperationException>(
            "a truncated dump is a named failure, never read; it was: {0}", failure?.ToString() ?? "no exception");
        failure!.Message.Should().Contain(dumpPath ?? "(the writer never ran)", "the failure names the truncated dump");
        File.Exists(dumpPath).Should().BeFalse("no dump file is left behind");
    }

    /// <summary>
    /// The ELF shape a LARGE dump takes — more program headers than <c>e_phnum</c> holds (createdump writes
    /// <c>0xFFFF</c> there and the real count in section 0's <c>sh_info</c>; CI dumps run to 300–500k pages).
    /// Built exactly as createdump lays it out: the length its headers describe is read through the section,
    /// so the whole file passes and the same file one byte short does not.
    /// </summary>
    [Fact]
    public void AnElfCoreWithMoreProgramHeadersThanItsHeaderHolds_IsMeasuredThroughItsSection()
    {
        const int programHeaders = 70_000, ehdr = 64, shdr = 64, phdr = 56;
        const long dataStart = ehdr + shdr + (long)programHeaders * phdr;
        const long described = dataStart + programHeaders - 1;
        var path = Path.Combine(Path.GetTempPath(), $"mw-elf-xnum-{Guid.NewGuid():N}.core");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 });
                writer.Write(new byte[8]);
                writer.Write((ushort)4);                 // e_type ET_CORE
                writer.Write((ushort)62);                // e_machine
                writer.Write(1u);                        // e_version
                writer.Write(0L);                        // e_entry
                writer.Write((long)(ehdr + shdr));       // e_phoff
                writer.Write((long)ehdr);                // e_shoff
                writer.Write(0u);                        // e_flags
                writer.Write((ushort)ehdr);              // e_ehsize
                writer.Write((ushort)phdr);              // e_phentsize
                writer.Write((ushort)0xFFFF);            // e_phnum = PN_XNUM
                writer.Write((ushort)shdr);              // e_shentsize
                writer.Write((ushort)1);                 // e_shnum
                writer.Write((ushort)0);                 // e_shstrndx
                var section = new byte[shdr];
                BitConverter.GetBytes((uint)programHeaders).CopyTo(section, 0x2C);
                writer.Write(section);
                for (var i = 0; i < programHeaders; i++)
                {
                    writer.Write(i == 0 ? 4u : 1u);      // PT_NOTE, then PT_LOAD
                    writer.Write(0u);
                    writer.Write(dataStart + Math.Max(i - 1, 0));
                    writer.Write(0L);
                    writer.Write(0L);
                    writer.Write(i == 0 ? 0L : 1L);      // p_filesz
                    writer.Write(i == 0 ? 0L : 1L);      // p_memsz
                    writer.Write(4096L);
                }
                writer.Write(new byte[programHeaders - 1]);
            }

            BlockedThreadStacks.DescribedLength(path).Should().Be(described, "the count is read from section 0, not e_phnum");
            Record.Exception(() => BlockedThreadStacks.EnsureWritten(path)).Should().BeNull("the whole file is every byte it describes");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
                file.SetLength(described - 1);
            Record.Exception(() => BlockedThreadStacks.EnsureWritten(path)).Should().BeOfType<InvalidOperationException>(
                "one byte short of what the headers describe is a cut dump");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The mirror of <see cref="AnElfCoreWithMoreProgramHeadersThanItsHeaderHolds_IsMeasuredThroughItsSection"/>
    /// for SECTIONS: <c>e_shnum == 0</c> with a section table means the real count is section 0's <c>sh_size</c>.
    /// The table sits at the end of the file, after every segment's data, so a file cut inside its tail is
    /// short only of section headers — and must still be refused.
    /// </summary>
    [Fact]
    public void AnElfCoreWithAnExtendedSectionCount_IsRefusedWhenCutInsideItsSectionTable()
    {
        const int ehdr = 64, phdr = 56, shdr = 64, sections = 0xFF00;
        const long dataStart = ehdr + phdr, dataLength = 8, sectionTable = dataStart + dataLength;
        const long described = sectionTable + (long)sections * shdr;
        var path = Path.Combine(Path.GetTempPath(), $"mw-elf-shnum-{Guid.NewGuid():N}.core");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 });
                writer.Write(new byte[8]);
                writer.Write((ushort)4);                 // e_type ET_CORE
                writer.Write((ushort)62);                // e_machine
                writer.Write(1u);                        // e_version
                writer.Write(0L);                        // e_entry
                writer.Write((long)ehdr);                // e_phoff
                writer.Write(sectionTable);              // e_shoff
                writer.Write(0u);                        // e_flags
                writer.Write((ushort)ehdr);              // e_ehsize
                writer.Write((ushort)phdr);              // e_phentsize
                writer.Write((ushort)1);                 // e_phnum
                writer.Write((ushort)shdr);              // e_shentsize
                writer.Write((ushort)0);                 // e_shnum = 0: the count is section 0's sh_size
                writer.Write((ushort)0);                 // e_shstrndx
                writer.Write(1u);                        // PT_LOAD
                writer.Write(0u);
                writer.Write(dataStart);                 // p_offset
                writer.Write(0L);
                writer.Write(0L);
                writer.Write(dataLength);                // p_filesz
                writer.Write(dataLength);                // p_memsz
                writer.Write(4096L);
                writer.Write(new byte[dataLength]);
                var section0 = new byte[shdr];
                BitConverter.GetBytes((long)sections).CopyTo(section0, 0x20);
                writer.Write(section0);
                writer.Write(new byte[(sections - 1) * shdr]);
            }

            BlockedThreadStacks.DescribedLength(path).Should().Be(described, "the section count is read from section 0's sh_size");
            Record.Exception(() => BlockedThreadStacks.EnsureWritten(path)).Should().BeNull("the whole file is every byte it describes");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
                file.SetLength(described - shdr / 2);
            Record.Exception(() => BlockedThreadStacks.EnsureWritten(path)).Should().BeOfType<InvalidOperationException>(
                "a file cut inside the tail of its section table is a cut dump, though every segment's data is there");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The same contract without a runtime: a writer that returns over no file fails the capture by name.</summary>
    [Fact]
    public void AWriterThatReturnsOverNoFile_FailsTheCaptureByName()
    {
        string? dumpPath = null;
        var stacks = new BlockedThreadStacks(writeDump: path => dumpPath = path);

        var failure = Record.Exception(() => stacks.CaptureThreadPoolWorkers());

        failure.Should().BeOfType<InvalidOperationException>("it was: {0}", failure?.ToString() ?? "no exception");
        failure!.Message.Should().Contain(dumpPath ?? "(the writer never ran)", "the failure names the dump that is missing");
    }
}
