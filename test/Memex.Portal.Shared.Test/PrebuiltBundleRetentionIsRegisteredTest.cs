using Memex.Portal.Shared;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #3963, pinned: the prebuilt-bundle retention sweep must be REGISTERED by the portal boot path.
///
/// <para>🚨 <b>A hosted service that exists, compiles and is tested proves nothing about whether it
/// RUNS.</b> <c>PrebuiltBundleRetentionHostedService</c> ships with unit tests for every keep rule,
/// a ledger, a report-only mode and a documented discipline — and all of that is inert on a
/// deployment whose host never constructs it. Nothing anywhere reports the absence, because "the
/// sweep found nothing to collect" and "the sweep never ran" produce the same observable: no
/// deletion, no ledger line, no log. That is the same shape as a CI gate that skips.</para>
///
/// <para><b>Why the assertion is on the DESCRIPTOR, and on <c>IHostedService</c> specifically.</b>
/// Registering the class as a plain singleton compiles, resolves, and never runs — the generic host
/// only starts what is registered AS <c>IHostedService</c>. So the check is not "the type exists"
/// and not "the type resolves": it is "the boot path put this implementation on the hosted-service
/// roster". Removing the <c>AddPrebuiltBundleRetention</c> line from <c>ConfigureMemexMesh</c>, or
/// downgrading it to a bare <c>AddSingleton</c>, fails this test.</para>
///
/// <para><b>The positive control.</b> The sibling collector of the same data volume
/// (<c>AddModuleGenerationsGc</c>, registered two lines above it) is asserted in the SAME
/// inspection. A harness that stopped reaching these registrations at all — a throwing boot path, a
/// renamed extension, a configuration shape that returns early — would otherwise let the subject's
/// absence read as a pass. Here it cannot: the control fails first and names itself.</para>
///
/// <para>🚨 This covers the REGISTRATION only. Whether the sweep then COLLECTS anything depends on
/// <c>PreWarm:PrebuiltBundleRoot</c> being configured, and whether it DELETES depends on
/// <c>PreWarm:PrebuiltBundleRetention:Delete</c> — whose code default is <c>true</c> and whose
/// chart default is <c>false</c>. <c>PrebuiltBundleRetentionArmingGuard</c> in
/// MeshWeaver.Hosting.Test holds that half. See Doc/Architecture/PrebuiltBundleRetention.</para>
/// </summary>
public class PrebuiltBundleRetentionIsRegisteredTest
{
    [Fact]
    public void TheBootPath_PutsTheRetentionSweep_OnTheHostedServiceRoster()
    {
        var root = Directory.CreateTempSubdirectory("mw-3963-").FullName;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Graph:Storage:Type"] = "FileSystem",
                    ["Graph:Storage:BasePath"] = root,
                    ["Modules:Root"] = root,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostApplicationLifetime>(
                new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance));

            // The REAL boot path — the one both portal hosts call
            // (Memex.Portal.Distributed/Program.cs, Memex.Portal.Monolith/Program.cs).
            new MeshBuilder(configure => configure(services), new Address("mesh", "test"))
                .ConfigureMemexMesh(configuration);

            // ── The positive control, asserted FIRST so a harness that reached no registration at
            //    all cannot let the subject's absence read as a pass. ────────────────────────────
            Assert.True(
                services.Any(d => d.ServiceType == typeof(ModuleGenerationsGcHostedService)),
                "POSITIVE CONTROL FAILED: ConfigureMemexMesh did not register the sibling "
                + "collector (AddModuleGenerationsGc) either, so this test observed NO boot-path "
                + "registrations and can say nothing about the prebuilt-bundle sweep. Fix the "
                + "harness — do not read this as the sweep being absent.");

            // ── The subject. ──────────────────────────────────────────────────────────────────
            Assert.True(
                services.Any(d => d.ServiceType == typeof(IHostedService)
                                  && d.ImplementationType == typeof(PrebuiltBundleRetentionHostedService)),
                "ConfigureMemexMesh must register the prebuilt-bundle retention sweep as an "
                + "IHostedService (services.AddPrebuiltBundleRetention(configuration), beside "
                + "AddModuleGenerationsGc). It is registered NOWHERE else: the sibling collector of "
                + "the same data volume is right there, so a deployment can mount "
                + "PreWarm:PrebuiltBundleRoot and have nothing at all sweep it. A store nothing "
                + "prunes filled memex.systemorph.com's 16 GiB /data to 3 MiB free on 2026-09-08, "
                + "and a full Azure Files share TRUNCATES WRITES SILENTLY (#3963).");

            // ── The rest of AddPrebuiltBundleRetention's contract: the policy the sweep reads and
            //    the status row an operator reads back. Either missing and the service cannot be
            //    constructed, which the host would surface only at StartAsync. ──────────────────
            Assert.True(
                services.Any(d => d.ServiceType == typeof(PrebuiltBundleRetention)),
                "AddPrebuiltBundleRetention must register the PrebuiltBundleRetention policy read "
                + "from configuration — without it the hosted service cannot be constructed.");
            Assert.True(
                services.Any(d => d.ServiceType == typeof(PrebuiltBundleRetentionStatus)),
                "AddPrebuiltBundleRetention must register PrebuiltBundleRetentionStatus — it is "
                + "where the last pass's result and fault are recorded for an operator to read.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* temp cleanup is the OS's problem, never a test failure */ }
        }
    }
}
