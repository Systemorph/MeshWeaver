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
    /// <summary>The client configuration assembled from hub configuration functions at construction time.</summary>
    public LayoutClientConfiguration Configuration { get; } = hub.Configuration
        .GetConfigurationFunctions()
        .Aggregate(new LayoutClientConfiguration(hub), (c,f) => f(c));

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
