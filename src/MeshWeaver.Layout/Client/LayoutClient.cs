using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Client;

/// <summary>
/// Contract for the client-side layout engine. Resolves a <see cref="ViewDescriptor"/> for
/// a given object instance and synchronization stream, driving the Blazor rendering pipeline.
/// </summary>
public interface ILayoutClient
{
    /// <summary>The message hub that hosts this layout client.</summary>
    IMessageHub Hub { get; }
    /// <summary>The aggregated client configuration assembled from hub configuration functions.</summary>
    public LayoutClientConfiguration Configuration { get; }
    /// <summary>
    /// Returns the view descriptor for <paramref name="instance"/> within the given
    /// <paramref name="stream"/> and <paramref name="area"/>, or <c>null</c> when no
    /// registered view matches.
    /// </summary>
    /// <param name="instance">The object to resolve a view for.</param>
    /// <param name="stream">The synchronization stream providing data context, or <c>null</c>.</param>
    /// <param name="area">The layout area name for which the view is being resolved.</param>
    /// <returns>A <see cref="ViewDescriptor"/> for the matched view, or <c>null</c>.</returns>
    ViewDescriptor? GetViewDescriptor(object instance, ISynchronizationStream<JsonElement>? stream, string area);
}

/// <summary>
/// Default <see cref="ILayoutClient"/> implementation. Builds the <see cref="LayoutClientConfiguration"/>
/// by aggregating all configuration functions registered on the hub, then delegates view
/// descriptor resolution to that configuration.
/// </summary>
public class LayoutClient(IMessageHub hub) : ILayoutClient
{
    /// <summary>The message hub that owns this layout client.</summary>
    public IMessageHub Hub => hub;
    /// <summary>
    /// The client configuration assembled from the hub's configuration functions and the CURRENT
    /// view registrations of any <see cref="IViewContributionSource"/> (the modules held in their own
    /// load contexts) — re-assembled whenever that source's version moves, so a live module swap
    /// brings its new views with it (policy <c>module-live-update-default</c>). With no source this is
    /// assembled once, as it always was.
    /// </summary>
    public LayoutClientConfiguration Configuration
    {
        get
        {
            var source = viewSource.Value;
            var version = source?.Version ?? 0;
            var built = Volatile.Read(ref configuration);
            if (built is not null && built.Version == version)
                return built.Configuration;
            var assembled = (source?.ViewConfigurations ?? [])
                .Aggregate(
                    hub.Configuration.GetConfigurationFunctions()
                        .Aggregate(new LayoutClientConfiguration(hub), (c, f) => f(c)),
                    (c, f) => f(c));
            Volatile.Write(ref configuration, new Assembled(version, assembled));
            return assembled;
        }
    }

    private sealed record Assembled(long Version, LayoutClientConfiguration Configuration);

    private Assembled? configuration;

    private readonly Lazy<IViewContributionSource?> viewSource =
        new(() => hub.ServiceProvider.GetService(typeof(IViewContributionSource)) as IViewContributionSource);

    /// <summary>
    /// Delegates to <see cref="LayoutClientConfiguration.GetViewDescriptor"/> to resolve a
    /// view descriptor for <paramref name="instance"/>.
    /// </summary>
    /// <param name="instance">The object to resolve a view for.</param>
    /// <param name="stream">The synchronization stream providing data context, or <c>null</c>.</param>
    /// <param name="area">The layout area name for which the view is being resolved.</param>
    /// <returns>A <see cref="ViewDescriptor"/> for the matched view, or <c>null</c>.</returns>
    public ViewDescriptor? GetViewDescriptor(object instance, ISynchronizationStream<JsonElement>? stream, string area)
        => Configuration.GetViewDescriptor(instance, stream, area);
}
