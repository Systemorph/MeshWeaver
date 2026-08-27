using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The instance manifest (#2550) — the durable answer to "which database, which modules boot,
/// which packages are provisioned, and which land for every user", written by the setup wizard on
/// an empty image and by a fleet Deployment record when it provisions one.
///
/// <para>These pin the properties that decide whether an instance SERVES, since every one of them
/// fails silently if it is wrong.</para>
/// </summary>
public class InstanceManifestTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-instance-" + Guid.NewGuid().ToString("N"));

    public InstanceManifestTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    [Fact]
    public void NoManifest_ReadsAsAbsent_NotAsAnError()
    {
        // Every deployment configured through appsettings today has no manifest. If absence were
        // an error — or anything other than null — this feature would break all of them at once.
        Assert.Null(InstanceManifest.Read(root));
    }

    [Fact]
    public void ARoundTrip_KeepsEveryAnswerTheWizardCollects()
    {
        var manifest = new InstanceManifest
        {
            State = InstanceSetupState.Complete,
            Storage = new InstanceStorageSelection
            {
                Type = "PostgreSql",
                SecretName = "memex-db-connection",
            },
            BootModules = ["MeshWeaver.Hosting.PostgreSql.dll"],
            ProvisionPackages = ["AI", "Store"],
            UserPreInstallPackages = ["MyAi"],
            SetUpBy = "rbuergi",
            SetUpAt = DateTimeOffset.Parse("2026-08-27T21:00:00Z"),
        };

        manifest.Write(root);
        var read = InstanceManifest.Read(root);

        Assert.NotNull(read);
        Assert.Equal(InstanceSetupState.Complete, read!.State);
        Assert.Equal("PostgreSql", read.Storage!.Type);
        Assert.Equal("memex-db-connection", read.Storage.SecretName);
        // The three module questions stay THREE lists: booting a DLL, provisioning a package into
        // the mesh, and landing one per user are different mechanisms, and collapsing them is how
        // a deployment ends up with content installed but no module to render it.
        Assert.Equal(["MeshWeaver.Hosting.PostgreSql.dll"], read.BootModules);
        Assert.Equal(["AI", "Store"], read.ProvisionPackages);
        Assert.Equal(["MyAi"], read.UserPreInstallPackages);
        Assert.Equal("rbuergi", read.SetUpBy);
    }

    [Fact]
    public void ACorruptManifest_ReadsAsUnreadable_NeverAsAbsent()
    {
        // 🚨 The distinction that protects an instance's DATA. Absent means "configured elsewhere,
        // carry on". Corrupt must NOT collapse to that: an instance that was set up once and now
        // reads as never-configured would offer a fresh setup wizard over a database that already
        // holds data, inviting an operator to point it somewhere new.
        File.WriteAllText(InstanceManifest.PathFor(root), "{ not json");
        string? reported = null;

        var read = InstanceManifest.Read(root, msg => reported = msg);

        Assert.NotNull(read);
        Assert.Equal(InstanceSetupState.Unreadable, read!.State);
        Assert.False(read.HasStorage, "an unreadable manifest answers nothing, so it cannot leave setup");
        Assert.NotNull(reported);
        Assert.Contains("SETUP", reported!);
    }

    [Fact]
    public void HasStorage_IsFalseUntilTheFirstQuestionIsAnswered()
    {
        Assert.False(new InstanceManifest().HasStorage);
        Assert.False(
            new InstanceManifest { Storage = new InstanceStorageSelection { Type = "  " } }.HasStorage,
            "a blank type is an unanswered question, not a backend named ' '");
        Assert.True(
            new InstanceManifest { Storage = new InstanceStorageSelection { Type = "FileSystem" } }
                .HasStorage);
    }

    [Fact]
    public void TheDefaultProfile_ProvisionsByPATTERN_NotAHandTypedList()
    {
        // 🚨 The failure this refuses. A default built as an enumerated list of package names is
        // stale the moment the next package ships, and its symptom lands nowhere near its cause:
        // the new package simply is not on a fresh instance — no error, no log line — and whoever
        // published it sees a working store with their package missing. The catalog already
        // understands source-scoped wildcards, so the default uses one.
        var manifest = InstanceSetupDefaults.Manifest();

        Assert.Equal(["Plugins/*"], manifest.ProvisionPackages);
        Assert.All(manifest.ProvisionPackages, p => Assert.Contains('/', p));
    }

    [Fact]
    public void TheDefaultProfile_MatchesWhatAWorkingDeploymentRuns()
    {
        var manifest = InstanceSetupDefaults.Manifest();

        // Postgres, because that is what every DEPLOYED installation runs — defaulting to the
        // file-system backend is how a portal ends up on container-ephemeral disk.
        Assert.Equal("PostgreSql", manifest.Storage!.Type);

        // gRPC boots from the image: it is the React GUI's browser data plane, not merely the
        // foreign-participant transport, so a fresh instance starts with it on.
        Assert.Contains("MeshWeaver.Hosting.Grpc.dll", manifest.BootModules);

        // Nothing lands per-user by default — a package's own preInstalled declaration is the
        // baseline, and anything beyond it spends every user's storage on their behalf.
        Assert.Empty(manifest.UserPreInstallPackages);
    }

    [Fact]
    public void RequiredModules_AreTheRegistryServedOnes_NeverBaselineAssemblies()
    {
        // The two lists are mutually exclusive for one name: a Modules:Assemblies entry SHADOWS a
        // landed store module (baseline wins, then dedupes by name), pinning the instance to an
        // app-closure copy the image may not ship at all.
        Assert.Empty(InstanceSetupDefaults.RequiredModules
            .Intersect(InstanceSetupDefaults.BootModules));
        Assert.Contains("MeshWeaver.AI.dll", InstanceSetupDefaults.RequiredModules);
    }

    [Fact]
    public void WriteIsAtomic_SoACrashMidWriteCannotStrandTheInstanceInSetup()
    {
        var first = new InstanceManifest
        {
            State = InstanceSetupState.Complete,
            Storage = new InstanceStorageSelection { Type = "FileSystem", BasePath = "/data/graph" },
        };
        first.Write(root);

        // A second write lands whole or not at all — never a truncated file that would read as
        // Unreadable and park a working instance in setup.
        new InstanceManifest
        {
            State = InstanceSetupState.Complete,
            Storage = new InstanceStorageSelection { Type = "PostgreSql", SecretName = "conn" },
        }.Write(root);

        var read = InstanceManifest.Read(root);
        Assert.Equal("PostgreSql", read!.Storage!.Type);
        // No temp files left behind to be mistaken for a manifest.
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }
}
