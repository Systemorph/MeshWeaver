using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A required module boot DEFERS is not "landed, a restart activates it".</b>
///
/// <para><b>The incident these pin.</b> A v4 control instance's <c>/health</c> read
/// <c>required_modules: Degraded — MeshWeaver.Blazor.Chat: store-delivered and landed on the volume,
/// not yet loaded in this process — a restart activates it.; MeshWeaver.Mcp: …</c>. The deployment
/// was restarted, and the new pods said exactly the same. Both modules were landed and enabled in the
/// line's activation record, with their bytes on the volume; boot projects that record onto the
/// mesh's proposed module set (#3395) and DEFERS every entry the set does not name, so no restart
/// could load them. The required-modules classification never read the set, and
/// <c>pending_module_activation</c> — which does read it — reported the deferral as Healthy, so it
/// was not even printed.</para>
///
/// <para><b>What makes these able to FAIL.</b> The record and the set are written by the real
/// writers (<see cref="ModuleActivationSidecar.WriteLanding"/>, <see cref="ModuleSetStore.Propose"/>),
/// the boot decision is the real <see cref="ModuleActivationBoot.ProjectOntoMeshSet"/>, and the
/// probe's inputs are the real <see cref="PendingModuleActivations.ReadProbeInputs"/> reading the
/// volume. The controls assert FIRST that boot really defers the module and the activation report
/// really names it deferred — so "the reason no longer promises a restart" cannot pass because the
/// module was out of the running for some other reason.</para>
/// </summary>
public class DeferredRequiredModuleNeverSaysRestartTest : IDisposable
{
    private const string Landed = "Acme.Engine";
    private const string Deferred = "Acme.Chat";
    private const string RestartPromise = "a restart activates it";

    private static readonly IReadOnlySet<string> NothingLoaded =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-deferred-required-" + Guid.NewGuid().ToString("N"));

    public DeferredRequiredModuleNeverSaysRestartTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The incident: one module in the proposed set, a second one landed AFTER the proposal and named
    /// by no set since. Boot defers the second; the required-modules verdict must say so, and must
    /// not promise a restart.
    /// </summary>
    [Fact]
    public void ALandedModuleTheSetDoesNotName_IsNotPromisedARestart()
    {
        LandGeneration(Landed, "0000000000000001");
        var proposed = ModuleSetStore.Propose(root, ModuleActivationSidecar.Read(root), proposedBy: "replica-a");
        var setId = Assert.IsType<ModuleSet>(proposed).Id;
        LandGeneration(Deferred, "0000000000000002");

        // Control 1: the BOOT defers it — the state the instance was in.
        var deferredByBoot = ImmutableList<string>.Empty;
        var onSet = ModuleActivationBoot.ProjectOntoMeshSet(
            ModuleActivationSidecar.Read(root), ModuleSetStore.Read(root).Proposed,
            (module, _) => deferredByBoot = deferredByBoot.Add(module),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry));
        Assert.Equal([Deferred], deferredByBoot);
        Assert.DoesNotContain(onSet.Entries, e => e.Name == Deferred);

        // Control 2: the activation report — the other reader of the same volume — names it deferred.
        var reader = new PendingModuleActivations(root);
        var report = reader.Read(NothingLoaded);
        Assert.Contains(report.Deferred, p => p.Name == Deferred);
        Assert.DoesNotContain(report.Pending, p => p.Name == Deferred);

        var verdict = Classify(reader, Deferred);

        Assert.Equal(RequiredModuleState.ExpectedLater, verdict.State);
        Assert.DoesNotContain(RestartPromise, verdict.Reason);
        Assert.Contains("NOT in the mesh's module set", verdict.Reason);
        Assert.Contains("a restart does NOT activate it", verdict.Reason);
        Assert.Contains(setId, verdict.Reason);
        Assert.Contains("replica-a", verdict.Reason);
    }

    /// <summary>Negative control: a landed module the set DOES name is genuinely pending, and keeps
    /// its restart promise — the fix narrows the sentence, it does not drop it.</summary>
    [Fact]
    public void ALandedModuleTheSetNames_IsStillPromisedARestart()
    {
        LandGeneration(Landed, "0000000000000001");
        Assert.NotNull(ModuleSetStore.Propose(root, ModuleActivationSidecar.Read(root), proposedBy: "replica-a"));
        var reader = new PendingModuleActivations(root);
        Assert.Contains(reader.Read(NothingLoaded).Pending, p => p.Name == Landed);

        var verdict = Classify(reader, Landed);

        Assert.Equal(RequiredModuleState.ExpectedLater, verdict.State);
        Assert.Contains(RestartPromise, verdict.Reason);
    }

    /// <summary>Negative control: with NO proposed set boot defers nothing (the identity
    /// projection), so a landed module is pending and the restart promise stands.</summary>
    [Fact]
    public void WithNoProposedSet_ALandedModuleIsStillPromisedARestart()
    {
        LandGeneration(Deferred, "0000000000000002");
        var reader = new PendingModuleActivations(root);
        Assert.Empty(reader.Read(NothingLoaded).Deferred);

        var verdict = Classify(reader, Deferred);

        Assert.Contains(RestartPromise, verdict.Reason);
    }

    /// <summary>The set is never written into the persisted record: it lives in its own records, and
    /// a bulk write of a list read beside it must not carry it into <c>activation.json</c>.</summary>
    [Fact]
    public void TheSetBesideTheRecord_IsNeverPersisted()
    {
        LandGeneration(Landed, "0000000000000001");
        Assert.NotNull(ModuleSetStore.Propose(root, ModuleActivationSidecar.Read(root), proposedBy: "replica-a"));
        var reader = new PendingModuleActivations(root);
        reader.Read(NothingLoaded);
        var activation = reader.ReadProbeInputs().Activation;
        Assert.NotNull(activation?.MeshModuleSet);

        var json = System.Text.Json.JsonSerializer.Serialize(activation);

        Assert.DoesNotContain(nameof(ModuleActivationList.MeshModuleSet), json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A legacy fixed-folder entry (<c>modules/&lt;name&gt;/</c> — no
    /// <see cref="ModuleActivationEntry.Directory"/>, so no generation to pin) passes through a
    /// proposed set UNCHANGED and is never deferred: <see cref="ModuleSetStore.GenerationsOf"/>
    /// excludes it by design, so "the set does not name it" says nothing about it.
    /// </summary>
    [Fact]
    public void ALegacyFixedFolderEntry_PassesThroughAProposedSet()
    {
        var legacy = new ModuleActivationEntry { Name = "Acme.Legacy", Directory = null };
        var deferred = ImmutableList<string>.Empty;

        var projected = ModuleActivationBoot.ProjectOntoMeshSet(
            new ModuleActivationList { Entries = [legacy] }, SetNaming(Landed),
            (module, _) => deferred = deferred.Add(module));

        Assert.Empty(deferred);
        Assert.Same(legacy, Assert.Single(projected.Entries));
        Assert.False(ModuleActivationBoot.IsDeferredBy(legacy, SetNaming(Landed)));
    }

    /// <summary>
    /// An entry the set does not name and that is NOT an enabled, named generation — disabled, or
    /// with a blank name — never reaches the set lookup: it passes through unchanged, exactly as
    /// before the deferral rule became a shared predicate. Boot must never throw on it.
    /// </summary>
    [Fact]
    public void AnEntryTheSetCannotName_PassesThroughAndNeverThrows()
    {
        var disabled = new ModuleActivationEntry
        {
            Name = "Acme.Uninstalled", Directory = "Acme.Uninstalled@0000000000000003", Enabled = false,
        };
        var blankName = new ModuleActivationEntry { Name = " ", Directory = "x@0000000000000004" };
        var deferred = ImmutableList<string>.Empty;

        var projected = ModuleActivationBoot.ProjectOntoMeshSet(
            new ModuleActivationList { Entries = [disabled, blankName] }, SetNaming(Landed),
            (module, _) => deferred = deferred.Add(module));

        Assert.Empty(deferred);
        Assert.Equal([disabled, blankName], projected.Entries);
    }

    /// <summary>
    /// 🚨 The probe and the boot share ONE rule: across every entry shape the projection meets,
    /// <see cref="ModuleActivationBoot.IsDeferredBy"/> is true for EXACTLY the entries the
    /// projection defers, and every other entry stays in the projected list.
    /// </summary>
    [Fact]
    public void IsDeferredBy_AgreesWithTheProjection_ForEveryEntryShape()
    {
        var set = SetNaming(Landed);
        ModuleActivationEntry[] entries =
        [
            new() { Name = Landed, Directory = Landed + "@0000000000000009" },
            new() { Name = Deferred, Directory = Deferred + "@0000000000000002" },
            new() { Name = "Acme.Legacy", Directory = null },
            new() { Name = "Acme.Blank", Directory = "  " },
            new() { Name = "Acme.Uninstalled", Directory = "Acme.Uninstalled@0000000000000003", Enabled = false },
            new() { Name = "", Directory = "x@0000000000000004" },
        ];
        var deferred = ImmutableList<string>.Empty;

        var projected = ModuleActivationBoot.ProjectOntoMeshSet(
            new ModuleActivationList { Entries = [.. entries] }, set, (module, _) => deferred = deferred.Add(module));

        Assert.Equal([Deferred], deferred);
        Assert.Equal(
            entries.Where(e => ModuleActivationBoot.IsDeferredBy(e, set)).Select(e => e.Name),
            deferred);
        Assert.Equal(entries.Length - deferred.Count, projected.Entries.Count);
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    /// <summary>A proposed set naming exactly <paramref name="module"/>.</summary>
    private static ModuleSet SetNaming(string module) =>
        new(1, "set-1",
            ImmutableSortedDictionary.CreateRange(StringComparer.OrdinalIgnoreCase,
                [KeyValuePair.Create(module, module + "@0000000000000001")]),
            DateTime.UtcNow);

    /// <summary>The probe exactly as the host's required-modules check asks it: the reader's own
    /// memoised inputs, nothing loaded, the module store-delivered (not in the image's list).</summary>
    private static RequiredModuleVerdict Classify(PendingModuleActivations reader, string module)
    {
        var inputs = reader.ReadProbeInputs();
        Assert.NotSame(ModuleProbeInputs.VolumeNotRead, inputs.ResolvesFromDeployment);
        return Assert.Single(RequiredModuleStatus.Classify(
            [module + ".dll"],
            [],
            NothingLoaded,
            inputs.ResolvesFromDeployment,
            inputs.Activation,
            inputs.LandedDllExists,
            _ => null,
            []));
    }

    /// <summary>Lands <paramref name="module"/> as the landing lane does: a generation directory
    /// holding its DLL, and the per-module record pointing at it.</summary>
    private void LandGeneration(string module, string id)
    {
        var generation = $"{module}@{id}";
        var directory = Path.Combine(root, "modules", generation);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, module + ".dll"), [0x4d, 0x5a]);
        Assert.True(ModuleActivationSidecar.WriteLanding(root,
            new ModuleLandingRecord { Name = module, Directory = generation, Version = "4.0.1" }),
            $"the landing record for {module} must be written");
    }
}
