using System.Globalization;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a registered instance says about itself when it talks to its registry — carried on the
/// token exchange (<see cref="SyncTokenPayloads.Request.Telemetry"/>) and on an error digest
/// (<see cref="ErrorDigestPayloads.Request.Telemetry"/>), stamped by the registry onto the
/// <see cref="Mesh.Security.MeshWeaverInstance"/> record as its <c>LastSeen*</c> fields
/// (<c>Doc/Architecture/InstanceTelemetry</c>).
///
/// <para>A CLAIM about a build, never an identity: the registry authenticates the key, not the body,
/// and reads nothing here for authorization. Every field is optional, so a consumer that predates
/// the record sends none and is served exactly as before. Every field is clipped to
/// <see cref="MaxFieldLength"/> on both sides (<see cref="Bounded"/>), so a misbehaving sender
/// cannot grow a record the admin list renders.</para>
/// </summary>
/// <param name="PlatformVersion">The platform build the instance runs — <c>PlatformBuildInfo.PlatformVersion</c>,
/// the same string its <c>/api/version</c> answers.</param>
/// <param name="Commit">The core commit that build was built from — <c>PlatformBuildInfo.CommitHash</c>.</param>
/// <param name="HomeUrl">The instance's own base URL (<c>PluginCatalog:HomeUrl</c>).</param>
/// <param name="Summary">A compact summary of what it carries — <see cref="InstanceTelemetryReader.Summarize"/>:
/// the package count and a digest of the package set, never the list.</param>
public sealed record InstanceTelemetry(
    string? PlatformVersion = null,
    string? Commit = null,
    string? HomeUrl = null,
    string? Summary = null)
{
    /// <summary>Longest value any field keeps; longer input is cut, never refused.</summary>
    public const int MaxFieldLength = 256;

    /// <summary>True when no field carries anything — nothing to stamp.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(PlatformVersion) && string.IsNullOrWhiteSpace(Commit)
        && string.IsNullOrWhiteSpace(HomeUrl) && string.IsNullOrWhiteSpace(Summary);

    /// <summary>The same telemetry with every field trimmed, blanks turned to <c>null</c> and
    /// anything longer than <see cref="MaxFieldLength"/> cut.</summary>
    public InstanceTelemetry Bounded() => new(Clip(PlatformVersion), Clip(Commit), Clip(HomeUrl), Clip(Summary));

    internal static string? Clip(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= MaxFieldLength ? trimmed : trimmed[..MaxFieldLength];
    }
}

/// <summary>
/// Composes THIS instance's <see cref="InstanceTelemetry"/>. The platform fields come straight off
/// the running assembly; the package summary is the one expensive part — a mesh read through
/// <see cref="InstanceComboReader"/>, the ONE reader of "what does this instance carry" — so it is
/// memoised for <see cref="SummaryFreshness"/> and bounded by <see cref="SummaryBudget"/>: a token
/// exchange is never held hostage by the inventory. When the read cannot answer in time the
/// telemetry goes out with the last known summary (or none), and says nothing false.
/// </summary>
public sealed class InstanceTelemetryReader(IMessageHub hub, ILogger<InstanceTelemetryReader> logger)
{
    /// <summary>How long one package summary is reused before the combo is read again.</summary>
    public static readonly TimeSpan SummaryFreshness = TimeSpan.FromMinutes(30);

    /// <summary>The most a telemetry read waits for the inventory before going without it.</summary>
    public static readonly TimeSpan SummaryBudget = TimeSpan.FromSeconds(5);

    private volatile SummaryReading? summary;

    private sealed record SummaryReading(string Summary, DateTimeOffset At);

    /// <summary>The telemetry as of now, with whatever summary is memoised — never blocks.</summary>
    public InstanceTelemetry Snapshot() =>
        new InstanceTelemetry(PlatformBuildInfo.PlatformVersion, PlatformBuildInfo.CommitHash, HomeUrl(), summary?.Summary).Bounded();

    /// <summary>
    /// The telemetry with a FRESH package summary when the memo is stale and the inventory answers
    /// inside <see cref="SummaryBudget"/>; otherwise <see cref="Snapshot"/>. Cold; emits once; never errors.
    /// </summary>
    public IObservable<InstanceTelemetry> Read()
    {
        if (summary is { } fresh && DateTimeOffset.UtcNow - fresh.At < SummaryFreshness)
            return Observable.Return(Snapshot());
        var combo = hub.ServiceProvider.GetService<InstanceComboReader>();
        if (combo is null)
            return Observable.Return(Snapshot());
        return Observable.Defer(() => combo.Read().Take(1).Timeout(SummaryBudget))
            .Do(read => summary = new SummaryReading(Summarize(read), DateTimeOffset.UtcNow))
            .Select(_ => Snapshot())
            .Catch((Exception exception) =>
            {
                // Debug, not Warning: the inventory is a nice-to-have on a token exchange, and a slow
                // mesh at boot would otherwise warn on every exchange until it settles.
                logger.LogDebug(exception,
                    "[InstanceTelemetry] the package summary did not answer within {Budget}; the telemetry goes "
                    + "out with the previous summary ({Summary})", SummaryBudget, summary?.Summary ?? "none");
                return Observable.Return(Snapshot());
            });
    }

    /// <summary>
    /// <c>"{count} packages · {digest}"</c> — the count of modules the instance carries and the first
    /// eight hex digits of a SHA-256 over the sorted <c>id@version</c> set, so two instances with the
    /// same packages at the same versions read the same, and a change anywhere in the set changes
    /// the digest. Order-independent; culture-invariant; carries no package name.
    /// </summary>
    public static string Summarize(InstanceCombo combo)
    {
        var entries = combo.Modules
            .Select(module => $"{module.ModuleId}@{module.Package?.Version ?? module.GitSync?.LastSyncCommitSha ?? ""}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))))[..8]
            .ToLowerInvariant();
        var noun = entries.Count == 1 ? "package" : "packages";
        return string.Create(CultureInfo.InvariantCulture, $"{entries.Count} {noun} · {digest}");
    }

    private string? HomeUrl()
    {
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>();
        if (!string.IsNullOrWhiteSpace(options?.HomeUrl))
            return options.HomeUrl.Trim();
        return hub.ServiceProvider.GetService<IConfiguration>()?["PluginCatalog:HomeUrl"]?.Trim();
    }
}
