using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// "Rows computed on the hub" from Doc/GUI/DataBinding → "Templates first, data later": a TEMPLATE
/// is emitted at once with its controls bound by pointer to <c>/data/{id}</c>, and a PROJECTION —
/// values the hub has to decide (a status line, a localized sentence) — is published there for as
/// long as the area lives. The control never waits for the projection, and never carries its values.
///
/// <para>This is <see cref="Template.Bind{T,TView}(IObservable{T},System.Linq.Expressions.Expression{Func{T,TView}}?,string?)"/>
/// without the expression rewriting: the templates here state their pointers explicitly, so a reader
/// sees exactly which field each control reads.</para>
/// </summary>
internal static class LayoutProjection
{
    /// <summary>
    /// Returns <paramref name="control"/> with a buildup that publishes <paramref name="projection"/>
    /// at <c>/data/{<paramref name="id"/>}</c> while the area it is rendered into lives. A fault is
    /// logged and ends the projection — the bound controls keep their last values.
    /// </summary>
    /// <typeparam name="TControl">The control type.</typeparam>
    /// <typeparam name="T">The projection's type.</typeparam>
    /// <param name="control">The template root.</param>
    /// <param name="id">The <c>/data</c> id the template's controls point into.</param>
    /// <param name="projection">The values, decided on the hub.</param>
    public static TControl PublishingTo<TControl, T>(this TControl control, string id, IObservable<T> projection)
        where TControl : UiControl
        => (TControl)control.WithBuildup((host, context, store) =>
        {
            var subscription = projection
                .DistinctUntilChanged()
                .Subscribe(
                    value => host.Stream.SetData(id, value, host.Stream.StreamId),
                    ex => host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(LayoutProjection))
                        .LogWarning(ex, "Projection {Id} for area {Area} faulted; its bound controls keep their last values",
                            id, context.Area));
            host.RegisterForDisposal(context.Area, subscription);
            return new(store, [], null);
        });
}
