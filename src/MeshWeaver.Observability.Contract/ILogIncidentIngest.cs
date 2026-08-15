namespace MeshWeaver.Observability;

/// <summary>
/// The ingest seam between the portal's HTTP surface and the red-log ticketing engine. The
/// portal's compiled endpoint code (<c>MapLogIncidents</c>) depends on THIS contract only, so the
/// engine (<c>MeshWeaver.Observability</c>, which implements it) can arrive compiled-in or as a
/// boot-loaded module (<c>Modules:Assemblies</c>) without the portal referencing it.
/// </summary>
public interface ILogIncidentIngest
{
    /// <summary>
    /// Ingests one aggregated burst report: dedupes against the structural identity, creates or
    /// merges the incident node, and kicks the triage control plane. Cold — the work runs on
    /// Subscribe.
    /// </summary>
    /// <param name="report">The burst report presented by the in-cluster watcher.</param>
    /// <returns>The outcome, emitted once the incident write has been accepted.</returns>
    IObservable<LogIncidentReportResult> Report(LogIncidentReport report);
}
