using System.Reactive.Linq;
using MeshWeaver.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout;

/// <summary>
/// Controls declared now and FED by a stream (Doc/GUI/DataBinding → "Templates first, data later"):
/// for text only the hub can compute — a status line, a rendered digest — the control is returned
/// at once, bound by pointer to <c>/data/{id}</c>, and the stream writes each value into that slot.
/// The grid counterpart is <see cref="DataGrid.DataGridBinding.BindGrid{TRow}"/>.
/// </summary>
public static class FeedBinding
{
    /// <summary>
    /// A <see cref="MarkdownControl"/> bound to <c>/data/{id}</c>, fed by <paramref name="markdown"/>.
    /// The feed is subscribed when the control is BUILT into its area (the seam
    /// <see cref="Template"/>'s binders use) and lives as long as that area; it writes
    /// <paramref name="loading"/> first, then every value, in ONE ordered subscription, so the
    /// loading text can never overwrite a value that arrived first.
    /// </summary>
    /// <param name="markdown">The markdown to show, as it changes.</param>
    /// <param name="id">The data id — stable per control within the area.</param>
    /// <param name="loading">Shown until the first value arrives (the loading shape).</param>
    /// <param name="failed">Shown when the feed fails, given the failure. The failure is also logged
    /// — reported, never swallowed; return a generic text where the page is ungated.</param>
    /// <returns>The bound control.</returns>
    public static MarkdownControl BindMarkdown(
        this IObservable<string> markdown, string id, string loading, Func<Exception, string> failed)
        => new MarkdownControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithBuildup((host, context, store) =>
            {
                host.RegisterForDisposal(context.Area, markdown
                    .StartWith(loading)
                    .DistinctUntilChanged()
                    .Subscribe(
                        value => host.UpdateData(id, value),
                        ex =>
                        {
                            host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                                .CreateLogger(typeof(FeedBinding))
                                .LogWarning(ex, "Markdown {Id} in area {Area}: its feed failed", id, context.Area);
                            host.UpdateData(id, failed(ex));
                        }));
                return new(store, [], null);
            });
}
