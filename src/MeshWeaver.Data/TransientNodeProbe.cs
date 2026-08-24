namespace MeshWeaver.Data;

/// <summary>
/// Marker declaring that a hub exists ONLY to be interrogated for type information and will be
/// disposed in the same breath — set via <c>AsTransientNodeProbe()</c> (MeshWeaver.Graph), read
/// wherever long-lived machinery would otherwise be installed on it.
///
/// <para>It lives HERE, not beside the extension that sets it, because
/// <see cref="DataContext.InitializeDataSources"/> must read it: a probe's data context is
/// <b>configured but its sources are never started</b>. Configuration (<see cref="DataContext.Initialize"/>,
/// inside <c>Build</c>) is what fills the type registry the probe reads; starting the sources is
/// what eagerly opens each one's synchronization stream — a <c>sync/</c> sub-hub apiece — on the
/// hub's init turn, machinery a hub that lives for microseconds has no use for. That init turn
/// RACES the probe's immediate dispose: usually creation wins, occasionally dispose wins and
/// <c>HostedHubsCollection</c> logs its "Rejecting hosted hub creation … during disposal" warning.
/// <c>ProbeHubCostTest</c> pins that warning as a teardown fault, and it flaked CI red twice on
/// 2026-08-22 (main <c>1b0b834</c> and PR #2062) — switching continuous delivery off both times,
/// since CD builds nothing while main's required check is red.</para>
///
/// <para>Streams are still created LAZILY when a request actually needs one — the guard removes
/// only the eager start nobody consumes.</para>
/// </summary>
/// <param name="StartDataSources">Whether the probe still STARTS its data sources on the init
/// turn. Default TRUE — the pre-existing behaviour, which probes that snapshot actual data (the
/// node-type MODEL probe reads instance content and schema through its sources) depend on:
/// skipping the start universally starved their <c>Initialized</c> tasks and six probe tests went
/// red (NodeTypeModelProbeTest, StaleStampRootBindingTest, IncrementalUpdateTest). Only a probe
/// that reads NOTHING BUT THE TYPE REGISTRY — the schema validation/lookup probes, whose eager
/// sync/ stream raced its own dispose into the CD-killing ProbeHubCostTest flake — opts out.</param>
public sealed record TransientNodeProbe(bool StartDataSources = true);
