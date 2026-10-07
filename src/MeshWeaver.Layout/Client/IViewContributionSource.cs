namespace MeshWeaver.Layout.Client;

/// <summary>
/// View registrations that can CHANGE while the process runs — the modules held in their own load
/// contexts (policy <c>module-live-update-default</c>): a live swap replaces a module's views, so the
/// layout client re-reads them whenever <see cref="Version"/> moves instead of folding them into a hub's
/// configuration once.
/// </summary>
public interface IViewContributionSource
{
    /// <summary>Moves whenever <see cref="ViewConfigurations"/> may have changed.</summary>
    long Version { get; }

    /// <summary>The current view registrations, in a stable order.</summary>
    IReadOnlyList<Func<LayoutClientConfiguration, LayoutClientConfiguration>> ViewConfigurations { get; }
}
