using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The READ-ONLY shared tier of <c>modules/</c> (Doc/Architecture/SharedArtifactStore, increment 3):
/// a landed generation's leaf is its content address, so the environment's control-owned store can
/// hold ONE copy every instance resolves. Pinned here: the instance's own copy wins; only a SEALED
/// shared copy is ever resolved; a consumer that finds one writes nothing to its own volume; the
/// owner publishes into the tier and falls back to its own volume when it cannot; nothing but a
/// generation is shared; and the per-instance GC never touches the tier.
/// </summary>
public class SharedModuleTierTest : IDisposable
{
    private const string Module = "Acme.Shared";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-shared-tier-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(root, "store", "linux-x64", "modules");
    private string Owner => Path.Combine(root, "owner");
    private string Consumer => Path.Combine(root, "consumer");

    public SharedModuleTierTest()
    {
        Directory.CreateDirectory(Owner);
        Directory.CreateDirectory(Consumer);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
        GC.SuppressFinalize(this);
    }

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);

    private static async Task<ModuleActivationEntry> Land(ModuleLandingService landing)
    {
        await landing.LandModule(Module, [(Module + ".dll", RealAssemblyBytes)], version: "1.0.0")
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        var list = await landing.GetActivation()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        return list.Entries.Single(e => e.Name == Module);
    }

    [Fact]
    public async Task TheOwner_PublishesTheGenerationIntoTheStore_SealedLast_AndKeepsNoLocalCopy()
    {
        using var owner = new ModuleLandingService(baseDirectory: Owner) { SharedRoot = Store, PublishesToShared = true };

        var entry = await Land(owner);

        var shared = Path.Combine(Store, entry.Directory!);
        Assert.True(File.Exists(Path.Combine(shared, Module + ".dll")), "the owner did not publish the generation into the store");
        Assert.Equal([Module + ".dll"], File.ReadAllLines(Path.Combine(shared, SharedModuleTier.SealFileName)));
        Assert.False(Directory.Exists(Path.Combine(Owner, "modules", entry.Directory!)),
            "the owner kept a second copy on its own volume");
        Assert.Equal(shared, ModuleLandingService.ModuleDirectoryInTier(Owner, Store, Module, entry));
    }

    [Fact]
    public async Task AConsumer_RecordsTheLandingAgainstTheSealedCopy_AndWritesNothingToItsVolume()
    {
        using (var owner = new ModuleLandingService(baseDirectory: Owner) { SharedRoot = Store, PublishesToShared = true })
            await Land(owner);
        using var consumer = new ModuleLandingService(baseDirectory: Consumer) { SharedRoot = Store, PublishesToShared = false };

        var entry = await Land(consumer);

        Assert.False(Directory.Exists(Path.Combine(Consumer, "modules", entry.Directory!)),
            "the consumer wrote its own copy although the store held the generation sealed");
        var resolved = ModuleLandingService.ModuleDirectoryInTier(Consumer, Store, Module, entry);
        Assert.Equal(Path.Combine(Store, entry.Directory!), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, Module + ".dll")));
    }

    [Fact]
    public async Task AConsumer_WithNoSealedCopy_LandsOnItsOwnVolume_AndNeverWritesTheStore()
    {
        Directory.CreateDirectory(Store);
        using var consumer = new ModuleLandingService(baseDirectory: Consumer) { SharedRoot = Store, PublishesToShared = false };

        var entry = await Land(consumer);

        Assert.True(File.Exists(Path.Combine(Consumer, "modules", entry.Directory!, Module + ".dll")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Store));
    }

    [Fact]
    public async Task AnOwnerThatCannotWriteTheStore_LandsOnItsOwnVolume_NeverFails()
    {
        // A FILE where the store's directory should be: every write into it is an IOException, the
        // shape of an unmounted or full share. The landing must still succeed — the tier is a saving.
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        File.WriteAllText(Store, "not a directory");
        using var owner = new ModuleLandingService(baseDirectory: Owner) { SharedRoot = Store, PublishesToShared = true };

        var entry = await Land(owner);

        Assert.True(File.Exists(Path.Combine(Owner, "modules", entry.Directory!, Module + ".dll")),
            "a store the owner could not write left the module unlanded");
    }

    [Fact]
    public void Resolution_TakesTheLocalCopyFirst_ThenOnlyASealedSharedOne()
    {
        const string leaf = Module + "@0123456789abcdef";
        var entry = new ModuleActivationEntry { Name = Module, Directory = leaf, Enabled = true };
        var local = Path.Combine(Consumer, "modules", leaf);
        var shared = Path.Combine(Store, leaf);

        // Unsealed in the store (a publication in flight): never resolved — the local miss stands.
        Directory.CreateDirectory(shared);
        Assert.Equal(local, ModuleLandingService.ModuleDirectoryInTier(Consumer, Store, Module, entry));

        File.WriteAllText(Path.Combine(shared, SharedModuleTier.SealFileName), Module + ".dll\n");
        // Sealed, but the file the seal lists is not there: a claim, not a copy — still the local miss.
        Assert.Equal(local, ModuleLandingService.ModuleDirectoryInTier(Consumer, Store, Module, entry));

        File.WriteAllBytes(Path.Combine(shared, Module + ".dll"), [1, 2, 3]);
        Assert.Equal(shared, ModuleLandingService.ModuleDirectoryInTier(Consumer, Store, Module, entry));

        // The instance's own copy always wins.
        Directory.CreateDirectory(local);
        Assert.Equal(local, ModuleLandingService.ModuleDirectoryInTier(Consumer, Store, Module, entry));

        // No tier, or a root still carrying its {rid} placeholder: exactly the local path.
        Directory.Delete(local);
        Assert.Equal(local, ModuleLandingService.ModuleDirectoryInTier(Consumer, null, Module, entry));
        Assert.Equal(local, ModuleLandingService.ModuleDirectoryInTier(Consumer, Path.Combine(root, "store", "{rid}", "modules"), Module, entry));
    }

    [Fact]
    public void OnlyAGeneration_IsEverShared_NeverTheMutableLegacyFolder()
    {
        // The legacy fixed folder modules/<name>/ is rewritten in place — never content-addressed.
        Directory.CreateDirectory(Path.Combine(Store, Module));
        File.WriteAllText(Path.Combine(Store, Module, SharedModuleTier.SealFileName), "");
        var legacy = new ModuleActivationEntry { Name = Module, Enabled = true };

        Assert.Equal(Path.Combine(Consumer, "modules", Module),
            ModuleLandingService.ModuleDirectoryInTier(Consumer, Store, Module, legacy));
        Assert.Throws<ArgumentException>(() => SharedModuleTier.Publish(Store, Module, [("x.dll", [1])]));
        Assert.False(SharedModuleTier.IsGeneration("../x@1"));
        Assert.False(SharedModuleTier.IsGeneration(".staging-x@1"));
    }

    [Fact]
    public async Task TheInstancesGarbageCollector_NeverDeletesFromTheStore()
    {
        using (var owner = new ModuleLandingService(baseDirectory: Owner) { SharedRoot = Store, PublishesToShared = true })
            await Land(owner);
        // A consumer that references NOTHING: every directory it could see would be garbage.
        Directory.CreateDirectory(Path.Combine(Consumer, "modules"));

        ModuleLandingService.CollectGarbage(Consumer, logger: null, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        Assert.Single(Directory.EnumerateDirectories(Store));
    }

    [Fact]
    public async Task ASealedButIncompleteCopy_IsNotUsed_AndTheOwnerRepairsIt()
    {
        ModuleActivationEntry entry;
        using (var owner = new ModuleLandingService(baseDirectory: Owner) { SharedRoot = Store, PublishesToShared = true })
            entry = await Land(owner);
        var shared = Path.Combine(Store, entry.Directory!);
        File.Delete(Path.Combine(shared, Module + ".dll"));   // an incomplete restore: the seal survived

        using (var consumer = new ModuleLandingService(baseDirectory: Consumer) { SharedRoot = Store, PublishesToShared = false })
            await Land(consumer);
        Assert.True(File.Exists(Path.Combine(Consumer, "modules", entry.Directory!, Module + ".dll")),
            "a consumer trusted a seal whose listed file is gone, and landed nothing of its own");

        using (var owner = new ModuleLandingService(baseDirectory: Owner) { SharedRoot = Store, PublishesToShared = true })
            await Land(owner);
        Assert.True(File.Exists(Path.Combine(shared, Module + ".dll")), "the owner did not restore the missing file");
        Assert.NotNull(SharedModuleTier.SealedDirectory(Store, entry.Directory));
    }

    [Fact]
    public async Task AnOwnerHoldingTheGenerationLocally_StillPublishesIt_OnceTheStoreIsThere()
    {
        // The generation landed before the tier existed (or while the store was down).
        ModuleActivationEntry entry;
        using (var before = new ModuleLandingService(baseDirectory: Owner))
            entry = await Land(before);
        Assert.True(Directory.Exists(Path.Combine(Owner, "modules", entry.Directory!)));

        using (var owner = new ModuleLandingService(baseDirectory: Owner) { SharedRoot = Store, PublishesToShared = true })
            await Land(owner);

        Assert.NotNull(SharedModuleTier.SealedDirectory(Store, entry.Directory));
        Assert.Equal(Path.Combine(Owner, "modules", entry.Directory!),
            ModuleLandingService.ModuleDirectoryInTier(Owner, Store, Module, entry));
    }

    [Fact]
    public void TheTier_IsConfiguredPerModuleRoot_NeverProcessWide()
    {
        var withTier = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                [SharedModuleTier.ConfigKey] = Store,
                [SharedModuleTier.PublishConfigKey] = "true",
            }).Build();
        var without = new ConfigurationBuilder().Build();

        SharedModuleTier.Configure(Owner, withTier);
        SharedModuleTier.Configure(Consumer, without);   // a second mesh in the same process

        Assert.Equal(new SharedModuleTier.Setting(Store, true), SharedModuleTier.For(Owner));
        Assert.Equal(new SharedModuleTier.Setting(null, false), SharedModuleTier.For(Consumer));
        using var landing = new ModuleLandingService(baseDirectory: Owner);
        Assert.Equal(Store, landing.SharedRoot);
        Assert.True(landing.PublishesToShared);
    }

    [Fact]
    public void AShareThatDropsBytes_SealsNothing_AndThePublicationFails()
    {
        const string leaf = Module + "@00112233445566ff";
        var bytes = new byte[32 * 1024];
        Array.Fill(bytes, (byte)0x5C);

        // Keeps 2 KiB of every file and drops the rest — the full Azure Files share that accepts a
        // write and loses it on writeback. The flush-and-length check must refuse it before the seal.
        Assert.ThrowsAny<IOException>(() => SharedModuleTier.PublishWith(Store, leaf, [(Module + ".dll", bytes)],
            path => new TruncatingStream(File.Create(path), 2048)));

        Assert.Null(SharedModuleTier.SealedDirectory(Store, leaf));
        Assert.False(File.Exists(Path.Combine(Store, leaf, SharedModuleTier.SealFileName)), "a truncated module was sealed");
    }

    /// <summary>Keeps the first <c>cap</c> bytes of every write and drops the rest, silently.</summary>
    private sealed class TruncatingStream(Stream inner, int cap) : Stream
    {
        private long accepted;

        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = (int)Math.Max(0, Math.Min(count, cap - accepted));
            if (room > 0)
                inner.Write(buffer, offset, room);
            accepted += count;
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
