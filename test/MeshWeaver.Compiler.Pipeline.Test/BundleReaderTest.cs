#pragma warning disable CS1591

using System.Text;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the bundle round trip — what <see cref="NuGetPackageWriter"/> writes,
/// <see cref="BundleReader"/> must read back.
///
/// <para>The two live in one library precisely so this holds, and it is worth pinning because every
/// way it can break is silent: a consumer that recovers the wrong node path seeds correct bytes
/// against the wrong NodeType, and the mismatch does not surface until activation throws a
/// <c>TypeLoadException</c> inside a collectible ALC — no compile error, no overlay, nothing to
/// grep.</para>
/// </summary>
public class BundleReaderTest
{
    private static readonly PluginManifest Manifest =
        new("ThreeBody", "MeshWeaver.Plugin.ThreeBody", "1.3.2", "ThreeBody", null, []);

    private static byte[] WriteBundle(params (string NodePath, byte[] Bytes)[] assemblies)
    {
        var entries = assemblies.Select(a => new NuGetPackageWriter.Entry(
            NuGetPackageWriter.EntryPathFor(a.NodePath),
            () => new MemoryStream(a.Bytes))).ToArray();

        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            assemblies = assemblies
                .Select(a => new { nodePath = a.NodePath, assembly = $"{a.NodePath}.dll" })
                .ToArray(),
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0", entries, manifestJson);
        return buffer.ToArray();
    }

    [Fact]
    public void APayloadComesBackAgainstItsOwnNodePath()
    {
        var bundle = WriteBundle(("ThreeBody/Physics/Source", "PHYSICS"u8.ToArray()));

        var (manifest, assemblies) = BundleReader.Read(bundle);

        Assert.Equal("ThreeBody", manifest!.Plugin);
        Assert.Equal("1.3.2", manifest.Version);
        Assert.Equal("33f2efb8aaaabbbbccccddddeeeeffff", manifest.FrameworkMvid);

        var only = Assert.Single(assemblies);
        Assert.Equal("ThreeBody/Physics/Source", only.NodePath);
        Assert.Equal("PHYSICS", Encoding.UTF8.GetString(only.Assembly));
        Assert.Null(only.Pdb);
    }

    [Theory]
    [InlineData("A/B/C", "meshweaver/assemblies/A/B/C.dll")]
    [InlineData("A_B/C", "meshweaver/assemblies/A_B/C.dll")]
    public void TheEntryPathIsTheNodePathVerbatim(string nodePath, string expected)
        // 🚨 The producer-side half of the collision guard: slash-replacing maps BOTH of these to
        // `A_B_C`. Pinned here rather than only in the round trip, because a writer that sanitises
        // produces a bundle the reader still parses happily — it just has one entry where two
        // belong, and the loser's NodeType silently adopts the winner's assembly.
        => Assert.Equal(expected, NuGetPackageWriter.EntryPathFor(nodePath));

    [Fact]
    public void PathsThatWouldCollideWhenSanitisedStaySeparate()
    {
        // 🚨 The regression this guards. Replacing '/' with '_' maps BOTH of these to
        // `ThreeBody_A_B` — one archive entry, one set of bytes, and the second NodeType silently
        // adopts the first one's assembly. Mesh paths do contain underscores, so this is reachable,
        // not theoretical.
        var bundle = WriteBundle(
            ("ThreeBody/A/B", "FIRST"u8.ToArray()),
            ("ThreeBody/A_B", "SECOND"u8.ToArray()));

        var (_, assemblies) = BundleReader.Read(bundle);

        Assert.Equal(2, assemblies.Count);
        Assert.Equal("FIRST", Encoding.UTF8.GetString(
            assemblies.Single(a => a.NodePath == "ThreeBody/A/B").Assembly));
        Assert.Equal("SECOND", Encoding.UTF8.GetString(
            assemblies.Single(a => a.NodePath == "ThreeBody/A_B").Assembly));
    }

    [Fact]
    public void AnAssemblyTheManifestNamesButTheArchiveLacksIsSkipped()
    {
        // Truncated or partially-assembled bundle: the rest must stay usable, because the NodeType
        // whose bytes are missing simply compiles — which is the behaviour without any bundle at all.
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8",
            assemblies = new[]
            {
                new { nodePath = "ThreeBody/Present", assembly = "ThreeBody/Present.dll" },
                new { nodePath = "ThreeBody/Absent", assembly = "ThreeBody/Absent.dll" },
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/Present.dll",
                    () => new MemoryStream("HERE"u8.ToArray())),
            ],
            manifestJson);

        var (_, assemblies) = BundleReader.Read(buffer.ToArray());

        var only = Assert.Single(assemblies);
        Assert.Equal("ThreeBody/Present", only.NodePath);
    }

    [Fact]
    public void SymbolsRideAlongWhenPresent()
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8",
            assemblies = new[] { new { nodePath = "ThreeBody/X", assembly = "ThreeBody/X.dll" } },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/X.dll",
                    () => new MemoryStream("DLL"u8.ToArray())),
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/X.pdb",
                    () => new MemoryStream("PDB"u8.ToArray())),
            ],
            manifestJson);

        var only = Assert.Single(BundleReader.Read(buffer.ToArray()).Assemblies);

        Assert.Equal("PDB", Encoding.UTF8.GetString(only.Pdb!));
    }

    /// <summary>
    /// 🚨 #4280 — the manifest's per-assembly <c>sourceVersions</c> (written by both bake paths
    /// since the fingerprint landed, skipped by every reader until now) comes back as the sorted
    /// KEY SET on the payload. The values are the producer's own clock and stay meaningless; the
    /// keys are which source nodes the bytes were built from, and they are what lets the owner
    /// tell a live set still ARRIVING from one that MOVED.
    /// </summary>
    [Fact]
    public void TheProducersSourcePathsRideAlongAsASortedKeySet()
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8",
            assemblies = new[]
            {
                new
                {
                    nodePath = "ThreeBody/X",
                    assembly = "ThreeBody/X.dll",
                    sourceFingerprint = "aa82137e45651a6c",
                    // Unsorted on purpose, with the tree bake's zero ticks — the reader sorts.
                    sourceVersions = new Dictionary<string, long>
                    {
                        ["ThreeBody/Source/Zeta"] = 0,
                        ["ThreeBody/X/Source/X"] = 0,
                        ["ThreeBody/Source/Alpha"] = 0,
                    },
                },
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/X.dll",
                    () => new MemoryStream("DLL"u8.ToArray())),
            ],
            manifestJson);

        var only = Assert.Single(BundleReader.Read(buffer.ToArray()).Assemblies);

        Assert.Equal("aa82137e45651a6c", only.SourceFingerprint);
        Assert.Equal(
            ["ThreeBody/Source/Alpha", "ThreeBody/Source/Zeta", "ThreeBody/X/Source/X"],
            only.SourcePaths!);
    }

    /// <summary>#4280 (second finding on #4293) — the manifest's <c>sourceIncludes</c> comes back
    /// sorted; absent on a legacy bundle.</summary>
    [Fact]
    public void TheProducersIncludePathsRideAlongAsASortedList()
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8",
            assemblies = new[]
            {
                new
                {
                    nodePath = "ThreeBody/X",
                    assembly = "ThreeBody/X.dll",
                    sourceFingerprint = "aa82137e45651a6c",
                    sourceVersions = new Dictionary<string, long> { ["ThreeBody/X/Source/X"] = 0 },
                    sourceIncludes = new[] { "ThreeBody/Shared/Usings", "ThreeBody/Shared/Header" },
                },
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/X.dll",
                    () => new MemoryStream("DLL"u8.ToArray())),
            ],
            manifestJson);

        var only = Assert.Single(BundleReader.Read(buffer.ToArray()).Assemblies);

        Assert.Equal(["ThreeBody/Shared/Header", "ThreeBody/Shared/Usings"], only.SourceIncludes!);
        Assert.Equal(["ThreeBody/X/Source/X"], only.SourcePaths!);
    }

    /// <summary>A legacy producer that recorded no snapshot yields <c>null</c> paths — the owner
    /// then judges on the fingerprint alone, exactly as before #4280.</summary>
    [Fact]
    public void ALegacyBundleWithoutSourceVersionsCarriesNoPaths()
    {
        var bundle = WriteBundle(("ThreeBody/Physics/Source", "PHYSICS"u8.ToArray()));

        var only = Assert.Single(BundleReader.Read(bundle).Assemblies);

        Assert.Null(only.SourcePaths);
        Assert.Null(only.SourceIncludes);
    }

    // ── the MODULE variant (#1664): one bundle, one reader, a second lane ──

    private static byte[] WriteModuleBundle(
        object module, params (string FileName, byte[] Bytes)[] moduleFiles)
    {
        var entries = moduleFiles.Select(f => new NuGetPackageWriter.Entry(
            NuGetPackageWriter.ModuleEntryPathFor(f.FileName),
            () => new MemoryStream(f.Bytes))).ToArray();

        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SocialMedia",
            version = "1.2.0",
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            module,
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0", entries, manifestJson);
        return buffer.ToArray();
    }

    [Fact]
    public void AModuleBundleRoundTrips()
    {
        var bundle = WriteModuleBundle(
            new
            {
                assemblyName = "MeshWeaver.Social",
                assemblies = new[] { "MeshWeaver.Social.dll" },
                minMeshVersion = "3.0.0",
            },
            ("MeshWeaver.Social.dll", "SOCIAL"u8.ToArray()));

        var (manifest, files) = BundleReader.ReadModule(bundle);

        Assert.Equal("MeshWeaver.Social", manifest!.Module!.AssemblyName);
        // The consumer's landing gate — a platform FLOOR, not the manifest-level frameworkMvid
        // (which stays the NodeType lane's strict gate and, for the module, diagnostics).
        Assert.Equal("3.0.0", manifest.Module.MinMeshVersion);
        var only = Assert.Single(files);
        Assert.Equal("MeshWeaver.Social.dll", only.FileName);
        Assert.Equal("SOCIAL", Encoding.UTF8.GetString(only.Bytes));
    }

    /// <summary>
    /// #5501 — a module read from a bundle ON DISK allocates each entry ONCE. The reader used to
    /// copy every entry into a growing <see cref="MemoryStream"/> and then <c>ToArray()</c> it —
    /// about three times the entry's size at the peak, on the large-object heap — the same dance that
    /// took the publish endpoint down with <c>OutOfMemoryException</c> in <c>MemoryStream.ToArray</c>.
    /// Synchronous and single-threaded on purpose, so the per-thread allocation counter measures
    /// exactly this read.
    /// </summary>
    [Fact]
    public void AModuleReadFromDiskAllocatesEachEntryOnce()
    {
        // Incompressible, so the archive's size tracks the entry's and Deflate hides nothing.
        var payload = new byte[8 * 1024 * 1024];
        new Random(5501).NextBytes(payload);
        var bundle = WriteModuleBundle(
            new { assemblyName = "MeshWeaver.Big", assemblies = new[] { "MeshWeaver.Big.dll" } },
            ("MeshWeaver.Big.dll", payload));

        var path = Path.Combine(Path.GetTempPath(), $"mw-5501-{Guid.NewGuid():N}.bundle");
        File.WriteAllBytes(path, bundle);
        try
        {
            using var file = File.OpenRead(path);
            // Warm-up read: JIT, the manifest's JSON metadata and ZipArchive's statics are one-time
            // costs, not what this measures.
            BundleReader.ReadModuleFrom(file);
            file.Position = 0;

            var before = GC.GetAllocatedBytesForCurrentThread();
            var (_, files) = BundleReader.ReadModuleFrom(file);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(payload, Assert.Single(files).Bytes);
            Assert.True(allocated < payload.Length * 3L / 2,
                $"reading one {payload.Length:N0}-byte entry allocated {allocated:N0} bytes — more "
                + "than 1.5x the entry, so the reader still grows a buffer and copies it out (#5501) "
                + "instead of allocating the declared length once");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// #5501 review — the declared length is the PRODUCER's claim, so an entry whose central
    /// directory advertises far more than its compressed bytes can expand to is refused BEFORE a
    /// buffer of that size is allocated. Without the bound, one malformed upload of a few hundred
    /// bytes would allocate ~2 GB on the publish endpoint.
    /// </summary>
    [Fact]
    public void AnEntryDeclaringMoreThanDeflateCanExpandIsRefusedBeforeAllocating()
    {
        var bundle = WriteModuleBundle(
            new { assemblyName = "MeshWeaver.Liar", assemblies = new[] { "MeshWeaver.Liar.dll" } },
            ("MeshWeaver.Liar.dll", "TINY"u8.ToArray()));
        PatchDeclaredLength(bundle, NuGetPackageWriter.ModuleEntryPathFor("MeshWeaver.Liar.dll"), 0x7FF00000);

        using var stream = new MemoryStream(bundle, writable: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => BundleReader.ReadModuleFrom(stream));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 16 * 1024 * 1024,
            $"refusing a lying entry allocated {allocated:N0} bytes — the declared length was trusted");
    }

    /// <summary>Overwrites the uncompressed-size field of one central-directory record.</summary>
    private static void PatchDeclaredLength(byte[] zip, string entryName, uint length)
    {
        var name = Encoding.UTF8.GetBytes(entryName);
        for (var i = 0; i + 46 <= zip.Length; i++)
        {
            if (BitConverter.ToUInt32(zip, i) != 0x02014b50)
                continue;
            var nameLength = BitConverter.ToUInt16(zip, i + 28);
            if (nameLength == name.Length && zip.AsSpan(i + 46, nameLength).SequenceEqual(name))
            {
                BitConverter.GetBytes(length).CopyTo(zip, i + 24);
                return;
            }
        }
        throw new InvalidOperationException($"no central-directory record for '{entryName}'");
    }

    [Fact]
    public void AMixedBundleServesBothLanes()
    {
        // The MeshWeaver.SocialMedia shape: content nodes + NodeType assemblies + a compiled module
        // in ONE Store product — so one bundle must serve both readers without either seeing the
        // other's payload.
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SocialMedia",
            version = "1.2.0",
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            assemblies = new[] { new { nodePath = "SocialMedia/Post", assembly = "SocialMedia/Post.dll" } },
            module = new { assemblyName = "MeshWeaver.Social", assemblies = new[] { "MeshWeaver.Social.dll" } },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.EntryPathFor("SocialMedia/Post"),
                    () => new MemoryStream("NODETYPE"u8.ToArray())),
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor("MeshWeaver.Social.dll"),
                    () => new MemoryStream("MODULE"u8.ToArray())),
            ],
            manifestJson);
        var bundle = buffer.ToArray();

        var nodeTypes = Assert.Single(BundleReader.Read(bundle).Assemblies);
        Assert.Equal("SocialMedia/Post", nodeTypes.NodePath);
        Assert.Equal("NODETYPE", Encoding.UTF8.GetString(nodeTypes.Assembly));

        var moduleFile = Assert.Single(BundleReader.ReadModule(bundle).Files);
        Assert.Equal("MODULE", Encoding.UTF8.GetString(moduleFile.Bytes));
    }

    [Fact]
    public void AnIncompleteModuleClosureYieldsNoFilesAtAll()
    {
        // All-or-nothing, deliberately UNLIKE the NodeType lane's skip: a NodeType with missing
        // bytes simply compiles, but a module folder missing part of its closure loads and then
        // faults at first use — a subset must never land.
        var bundle = WriteModuleBundle(
            new
            {
                assemblyName = "MeshWeaver.Social",
                assemblies = new[] { "MeshWeaver.Social.dll", "MeshWeaver.Social.Support.dll" },
            },
            ("MeshWeaver.Social.dll", "SOCIAL"u8.ToArray()));

        var (manifest, files) = BundleReader.ReadModule(bundle);

        Assert.NotNull(manifest!.Module);
        Assert.Empty(files);
    }

    [Fact]
    public void ANodeTypeOnlyBundleDeclaresNoModule()
    {
        var bundle = WriteBundle(("ThreeBody/Physics/Source", "PHYSICS"u8.ToArray()));

        var (manifest, files) = BundleReader.ReadModule(bundle);

        Assert.Null(manifest!.Module);
        Assert.Empty(files);
    }

    [Fact]
    public void AnArchiveWithoutAManifestYieldsNothing()
    {
        // Not an exception: a consumer must treat "cannot read this" the same as "no bundle" and
        // compile. Throwing here would turn a distribution problem into a failed install.
        var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("readme.txt").Open();
            stream.Write("not a bundle"u8);
        }

        var (manifest, assemblies) = BundleReader.Read(buffer.ToArray());

        Assert.Null(manifest);
        Assert.Empty(assemblies);
    }

    [Fact]
    public void AManifestWithNoFrameworkIdentityReadsAsNull()
    {
        // The seeder DECLINES on a null MVID, so this value must survive as null rather than
        // becoming an empty string that a careless comparison could treat as a match.
        var manifestJson = JsonSerializer.Serialize(new { plugin = "ThreeBody", version = "1.3.2" });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0", [], manifestJson);

        Assert.Null(BundleReader.Read(buffer.ToArray()).Manifest!.FrameworkMvid);
    }
    /// <summary>
    /// 🚨 The READER enforces the native layout too, not only the packer (#4126). This is the
    /// boundary for a PRODUCER-controlled bundle and the landing stage writes these paths to disk
    /// for the process to LOAD — so "not traversing" is not enough: a payload at a path the loader
    /// never probes would be written and then never looked at, shipped in appearance and absent in
    /// behaviour. Refused, with the two cases kept apart because they are different statements.
    /// </summary>
    [Theory]
    [InlineData("runtimes/linux-x64/other/native/deep.so", "not the layout")]
    [InlineData("runtimes/linux-x64/native/sub/nested.so", "not the layout")]
    [InlineData("../escape.so", "unsafe native path")]
    public void ANativePayloadOutsideTheProbedLayoutIsREFUSED(string declared, string says)
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.0.0",
            frameworkMvid = "abc",
            module = new
            {
                assemblyName = "M",
                assemblies = new[] { "M.dll" },
                nativeAssets = new[] { declared },
            },
        });
        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
        [
            new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleEntryPathFor("M.dll"),
                () => new MemoryStream("M"u8.ToArray())),
        ], manifestJson);
        var bundle = buffer.ToArray();

        var thrown = Assert.Throws<InvalidOperationException>(
            () => BundleReader.ReadModuleNativeAssets(bundle));
        Assert.Contains(says, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 A BUNDLE IS READ BY PORTALS RUNNING OLDER IMAGES, and #4126 added a whole new section to
    /// the format. Whether that is ADDITIVE — an older reader ignores it — or a CHANGE OF MEANING
    /// is the question that decides whether the format needs a VERSION, and until this test nothing
    /// asserted it. The registry serves one set of bytes to every installation, so the answer is not
    /// something a producer-side test can reach: it is a property of what the OLD reader does.
    ///
    /// <para>The verdict is <b>additive, no format version required</b>, and it rests on two
    /// independent facts, both pinned below rather than reasoned about:</para>
    ///
    /// <list type="number">
    /// <item><b>The sections are PREFIX-DISJOINT.</b> Every consumer of the flat module folder
    /// filters by <c>meshweaver/modules/</c> with no <c>/</c> in the remainder
    /// (<c>ServedModuleBytes</c>, <c>PublishedBundleCatalogue</c>), and the asset consumer by
    /// <c>meshweaver/moduleassets/</c>. A native lands under neither prefix, so an older reader
    /// does not skip it, mis-file it, or fail on it — it never enumerates it at all.</item>
    /// <item><b>The manifest field is UNMAPPED, and unmapped is SKIPPED.</b>
    /// <c>BundleReader</c> deserializes with <c>JsonSerializerDefaults.Web</c>, whose
    /// <c>UnmappedMemberHandling</c> is <c>Skip</c>, so an older <c>ModuleRef</c> — which has no
    /// <c>nativeAssets</c> property at all — reads the rest of the manifest unchanged.</item>
    /// </list>
    ///
    /// <para>🚨 What WOULD have needed a format version, and is the reason the section is its own:
    /// putting natives under <c>meshweaver/modules/</c> changes that folder's MEANING — an older
    /// reader's flat filter silently drops them while the producer believes it shipped them — and
    /// putting them under <c>meshweaver/moduleassets/</c> would have an older reader lay a loadable
    /// binary into <c>wwwroot</c> and SERVE it over HTTP.</para>
    /// </summary>
    [Fact]
    public void ABundleCarryingNativesIsReadUnchangedByAPreNativesReader()
    {
        const string NativePath = "runtimes/linux-x64/native/libe_sqlite3.so";
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            module = new
            {
                assemblyName = "M",
                assemblies = new[] { "M.dll" },
                minMeshVersion = "3.0.0",
                staticAssets = new[] { "wwwroot/app.js" },
                nativeAssets = new[] { NativePath },
            },
        });
        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
        [
            new NuGetPackageWriter.Entry(NuGetPackageWriter.ModuleEntryPathFor("M.dll"),
                () => new MemoryStream("M"u8.ToArray())),
            new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleAssetEntryPathFor("wwwroot/app.js"),
                () => new MemoryStream("js"u8.ToArray())),
            new NuGetPackageWriter.Entry(NuGetPackageWriter.ModuleNativeEntryPathFor(NativePath),
                () => new MemoryStream("elf"u8.ToArray())),
        ], manifestJson);
        var bundle = buffer.ToArray();

        // ---- (1) the ARCHIVE: an older reader's prefix filter never reaches the new section ----
        using var archive = new System.IO.Compression.ZipArchive(
            new MemoryStream(bundle, writable: false), System.IO.Compression.ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToArray();

        // 🚨 THE CONTROL FIRST. Without it every assertion below would pass over a bundle that
        // simply carries no native — "the older reader ignored it" and "there was nothing to
        // ignore" are the same green.
        var native = Assert.Single(
            names.Where(n => n.EndsWith("libe_sqlite3.so", StringComparison.Ordinal)));

        // 🚨 …AND IT IS AT THE DECLARED PATH, not merely somewhere outside the other two (#4318
        // review). Finding it by file name and then checking only that it is NOT under the old
        // prefixes leaves a writer free to emit it under any THIRD prefix and still pass — a test
        // claiming to pin the format contract while pinning only two thirds of it. The path IS the
        // contract here: `ModuleNativeAssets` probes `<moduleDir>/runtimes/<rid>/native/<lib>`, so
        // the section name and the preserved relative path are both load-bearing.
        Assert.Equal(NuGetPackageWriter.ModuleNativeEntryPathFor(NativePath), native);

        // The predicate every pre-#4126 consumer of the flat folder spells, driven off the SAME
        // constants those consumers use so the assertion cannot drift away from them.
        static bool FlatModuleEntry(string name) =>
            name.StartsWith(NuGetPackageWriter.ModuleFolder + "/", StringComparison.Ordinal)
            && !name[(NuGetPackageWriter.ModuleFolder.Length + 1)..].Contains('/');

        Assert.Equal([NuGetPackageWriter.ModuleEntryPathFor("M.dll")],
            names.Where(FlatModuleEntry).ToArray());
        Assert.False(FlatModuleEntry(native));
        // …and it is not under the flat folder AT ALL — not merely filtered out of it. The
        // difference matters: an entry under `meshweaver/modules/` that the filter drops is bytes a
        // producer believes it shipped and a consumer silently skips.
        Assert.DoesNotContain(NuGetPackageWriter.ModuleFolder + "/", native, StringComparison.Ordinal);
        Assert.DoesNotContain(NuGetPackageWriter.ModuleAssetFolder + "/", native, StringComparison.Ordinal);
        // The asset consumer is untouched too: its tree is exactly what it was.
        Assert.Equal([NuGetPackageWriter.ModuleAssetEntryPathFor("wwwroot/app.js")],
            names.Where(n => n.StartsWith(NuGetPackageWriter.ModuleAssetFolder + "/",
                StringComparison.Ordinal)).ToArray());

        // ---- (2) the MANIFEST: an older ModuleRef has no such property, and Skip is the default --
        // 🚨 From the STREAM, exactly as BundleReader does. The manifest entry is written with a
        // UTF-8 BOM, and `Utf8JsonReader` skips one over a stream but NOT over a span — so reading
        // the bytes directly fails with "'0xEF' is an invalid start of a value" and would have this
        // test measuring the wrong thing entirely.
        var manifestBytes = ReadEntry(archive, NuGetPackageWriter.ManifestEntry);
        Assert.NotNull(manifestBytes);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var older = JsonSerializer.Deserialize<PreNativesManifest>(
            new MemoryStream(manifestBytes!, writable: false), web);

        Assert.NotNull(older);
        Assert.Equal("ThreeBody", older!.Plugin);
        Assert.Equal("1.3.2", older.Version);
        Assert.Equal("33f2efb8aaaabbbbccccddddeeeeffff", older.FrameworkMvid);
        Assert.Equal("M", older.Module!.AssemblyName);
        Assert.Equal(["M.dll"], older.Module.Assemblies);
        Assert.Equal("3.0.0", older.Module.MinMeshVersion);
        Assert.Equal(["wwwroot/app.js"], older.Module.StaticAssets);

        // 🚨 AND THE POSITIVE CONTROL FOR (2). The assertion above is only evidence about SKIP if
        // the JSON really carries the unmapped member — otherwise it passes having deserialized a
        // manifest with nothing new in it. Disallow must therefore THROW on the very same bytes.
        var strict = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<PreNativesManifest>(
                new MemoryStream(manifestBytes!, writable: false), strict));
    }

    /// <summary>The manifest shape as it stood BEFORE #4126 — no <c>nativeAssets</c> anywhere. This
    /// is the older reader, and it is spelled out here rather than referenced so that adding a
    /// property to the real <see cref="BundleReader.Manifest"/> can never quietly update it.</summary>
    private sealed record PreNativesManifest(
        string? Plugin, string? Version, string? FrameworkMvid, PreNativesModule? Module);

    private sealed record PreNativesModule(
        string? AssemblyName, IReadOnlyList<string>? Assemblies, string? MinMeshVersion,
        IReadOnlyList<string>? StaticAssets);

    private static byte[]? ReadEntry(System.IO.Compression.ZipArchive archive, string name)
    {
        var entry = archive.Entries.FirstOrDefault(
            e => e.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

}
