using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.PluginTester;

/// <summary>The outcome of rendering one layout area, with a human-readable detail line.</summary>
/// <param name="Outcome">Pass / fail / skip.</param>
/// <param name="Detail">The verdict text (the pass summary, or the failing rows / error).</param>
public sealed record AreaVerdict(CheckOutcome Outcome, string? Detail);

/// <summary>
/// Headless layout-area probes over the standard client sync stream
/// (<c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;</c> — the same wire the portal
/// binds). Rendering a plugin type's <c>Tests</c> area EXECUTES its in-node test suite (the
/// area function runs the assertions and renders a ✅/❌ table with an <c>N/M passed</c>
/// summary — the convention the plugins repo's AGENTS.md mandates); the probe waits for a
/// terminal verdict frame and classifies it.
/// </summary>
public static class AreaProbe
{
    // The framework's visible failure shapes (LayoutAreaHost.FailRendering / RenderEmergency /
    // LayoutDefinition.NotFound) — a rendered control carrying one of these IS the red signal.
    private const string AreaNotFoundMarker = "Area not found";
    private const string RenderFailedMarker = "This area failed to render";
    private const string RenderEmergencyMarker = "cannot be rendered";

    private static readonly Regex PassSummary = new(
        @"(\d+)\s*/\s*(\d+)\s+passed", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Renders the node's DEFAULT area and classifies the first materialised frame: green when
    /// the resolved area's control landed without a framework error control, red on an error
    /// control or when nothing materialises within <paramref name="timeout"/>.
    ///
    /// <para>🚨 An <c>Area not found</c> frame is TRANSIENT, never terminal — see
    /// <see cref="ExecuteTestsArea"/> for the mechanism (layout re-registration after a fresh
    /// compile). The stream keeps waiting for a frame in which the area exists; the timeout is
    /// the backstop, and it reports the last transient state so a genuinely missing area still
    /// fails with the precise reason.</para>
    /// </summary>
    public static IObservable<AreaVerdict> RenderDefaultArea(
        IMessageHub client, string nodePath, TimeSpan timeout) =>
        Observable.Defer(() =>
        {
            string? lastTransient = null;
            return Frames(client, nodePath, area: null)
                .Where(frame => IsAreaMaterialized(frame, requestedArea: null))
                .Select(frame =>
                {
                    var strings = CollectStrings(frame);
                    var notFound = strings.FirstOrDefault(s =>
                        s.Contains(AreaNotFoundMarker, StringComparison.Ordinal));
                    if (notFound is not null)
                    {
                        lastTransient = FirstLines(notFound);
                        return null;   // keep waiting — registration may still be catching up
                    }
                    var error = strings.FirstOrDefault(s =>
                        s.Contains(RenderFailedMarker, StringComparison.Ordinal)
                        || s.Contains(RenderEmergencyMarker, StringComparison.Ordinal));
                    return error is null
                        ? new AreaVerdict(CheckOutcome.Passed, null)
                        : new AreaVerdict(CheckOutcome.Failed, FirstLines(error));
                })
                .Where(verdict => verdict is not null)
                .Select(verdict => verdict!)
                .Take(1)
                .Timeout(timeout)
                .Catch((TimeoutException _) => Observable.Return(new AreaVerdict(
                    CheckOutcome.Failed,
                    lastTransient is null
                        ? $"default area did not materialise within {timeout.TotalSeconds:F0}s"
                        : $"default area never became available within {timeout.TotalSeconds:F0}s — "
                          + $"last state: {lastTransient}")));
        });

    /// <summary>
    /// Renders (= EXECUTES) the <c>Tests</c> layout area on <paramref name="nodePath"/> and
    /// waits for a terminal verdict frame: any ❌ row or framework error control is red; an
    /// all-green <c>N/N passed</c> summary (or ✅ rows without ❌) is green. No verdict within
    /// <paramref name="timeout"/> is red — a Tests area that reports nothing is a broken gate,
    /// never a silent pass.
    /// </summary>
    public static IObservable<AreaVerdict> ExecuteTestsArea(
        IMessageHub client, string nodePath, TimeSpan timeout) =>
        ClassifyTestsFrames(Frames(client, nodePath, area: "Tests"), timeout);

    /// <summary>
    /// The Tests-area verdict pipeline over an arbitrary frame stream — the seam the tests drive
    /// with synthetic frames, and <see cref="ExecuteTestsArea"/> with the live sync stream.
    /// </summary>
    public static IObservable<AreaVerdict> ClassifyTestsFrames(
        IObservable<JsonElement> frames, TimeSpan timeout) =>
        Observable.Defer(() =>
        {
            // 🚨 "Area not found" is a TRANSIENT frame here, never a terminal verdict. Right after
            // a (re)compile the instance hub re-registers its layout, so the sync stream can
            // legitimately serve a frame in which the type's custom areas do not exist YET — only
            // the defaults. Latching that first frame with Take(1) turned the re-registration
            // window into a gate failure ("No renderer is registered for area `Tests` on hub
            // `Store`") that fired only on loaded CI runners — 16 straight local runs, macOS and
            // Linux alike, could not reproduce it, and it redded three unrelated core PRs in one
            // day. The platform's own rule is to wait on the actual condition via the stream: keep
            // waiting for a frame that carries the area; the timeout stays the backstop, and it
            // reports the last transient state so a genuinely absent Tests area still fails with
            // the precise reason instead of a generic "no verdict".
            string? lastTransient = null;
            return frames
                .Select(frame => ClassifyTestsFrame(frame, transient => lastTransient = transient))
                .Where(verdict => verdict is not null)
                .Select(verdict => verdict!)
                .Take(1)
                .Timeout(timeout)
                .Catch((TimeoutException _) => Observable.Return(new AreaVerdict(
                    CheckOutcome.Failed,
                    lastTransient is null
                        ? $"Tests area reported no verdict within {timeout.TotalSeconds:F0}s " +
                          "(expected a ✅/❌ table with an 'N/M passed' summary)"
                        : $"Tests area never became available within {timeout.TotalSeconds:F0}s — "
                          + $"last state: {lastTransient}")));
        });

    // One cold frame stream per probe; the sync stream is disposed on unsubscribe/terminal.
    private static IObservable<JsonElement> Frames(IMessageHub client, string nodePath, string? area) =>
        Observable.Defer(() =>
        {
            var stream = client.GetWorkspace()
                .GetRemoteStream<JsonElement, LayoutAreaReference>(
                    new Address(nodePath), new LayoutAreaReference(area) { Id = "" });
            return stream.Select(ci => ci.Value).Finally(stream.Dispose);
        });

    // Terminal classification of a Tests frame; null = keep waiting (still rendering, or the
    // area not registered yet — the transient detail is reported through onTransient so the
    // timeout verdict can carry it).
    private static AreaVerdict? ClassifyTestsFrame(JsonElement frame, Action<string> onTransient)
    {
        var strings = CollectStrings(frame);

        var notFound = strings.FirstOrDefault(s => s.Contains(AreaNotFoundMarker, StringComparison.Ordinal));
        if (notFound is not null)
        {
            onTransient(FirstLines(notFound));
            return null;
        }

        var renderError = strings.FirstOrDefault(s =>
            s.Contains(RenderFailedMarker, StringComparison.Ordinal)
            || s.Contains(RenderEmergencyMarker, StringComparison.Ordinal));
        if (renderError is not null)
            return new AreaVerdict(CheckOutcome.Failed, FirstLines(renderError));

        var red = strings.FirstOrDefault(s => s.Contains('❌'));
        if (red is not null)
            return new AreaVerdict(CheckOutcome.Failed, FailingRows(red));

        foreach (var candidate in strings)
        {
            var summary = PassSummary.Match(candidate);
            if (summary.Success)
            {
                var passed = int.Parse(summary.Groups[1].Value);
                var total = int.Parse(summary.Groups[2].Value);
                return passed == total
                    ? new AreaVerdict(CheckOutcome.Passed, $"{passed}/{total} passed")
                    : new AreaVerdict(CheckOutcome.Failed, $"only {passed}/{total} passed");
            }
        }

        // A green table without the summary line still counts (❌ was excluded above).
        return strings.Any(s => s.Contains('✅'))
            ? new AreaVerdict(CheckOutcome.Passed, "all rendered cases green")
            : null;
    }

    /// <summary>
    /// True when the frame carries the requested area's rendered control — and, for a
    /// default-area subscription, the resolved area the <c>areas[""]</c> indirection points at.
    /// Mirrors the AI mesh-operations render probe (<c>MeshOperations.IsAreaMaterialized</c>).
    /// </summary>
    private static bool IsAreaMaterialized(JsonElement store, string? requestedArea)
    {
        if (store.ValueKind != JsonValueKind.Object
            || !store.TryGetProperty(LayoutAreaReference.Areas, out var areas)
            || areas.ValueKind != JsonValueKind.Object)
            return false;

        var rootKey = requestedArea ?? string.Empty;
        if (!TryGetWireInstance(areas, rootKey, out var control))
            return false;

        if (rootKey.Length == 0
            && control.ValueKind == JsonValueKind.Object
            && (control.TryGetProperty("area", out var resolved)
                || control.TryGetProperty("Area", out resolved))
            && resolved.ValueKind == JsonValueKind.String
            && resolved.GetString() is { Length: > 0 } resolvedArea)
            return TryGetWireInstance(areas, resolvedArea, out _);

        return true;
    }

    // InstanceCollection keys ride JSON-encoded on the wire ("Tests" → property "\"Tests\"").
    private static bool TryGetWireInstance(JsonElement collection, string key, out JsonElement value)
    {
        if (collection.TryGetProperty(JsonSerializer.Serialize(key), out value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return true;
        value = default;
        return false;
    }

    // Every string VALUE in the frame, unescaped — the classification scans real text, not
    // wire-escaped JSON.
    private static IReadOnlyList<string> CollectStrings(JsonElement element)
    {
        var strings = new List<string>();
        Walk(element, strings);
        return strings;

        static void Walk(JsonElement node, List<string> into)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                    if (node.GetString() is { Length: > 0 } s)
                        into.Add(s);
                    break;
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject())
                        Walk(property.Value, into);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                        Walk(item, into);
                    break;
            }
        }
    }

    // The ❌ rows (plus the summary) of a red table — the actionable lines, not the whole page.
    private static string FailingRows(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n')
            .Where(l => l.Contains('❌') || PassSummary.IsMatch(l))
            .ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : FirstLines(markdown);
    }

    private static string FirstLines(string text, int count = 6)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return string.Join("\n", lines.Take(count));
    }
}
