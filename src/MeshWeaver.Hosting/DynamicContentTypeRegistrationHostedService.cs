using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Runs <see cref="DynamicContentTypeRegistrar.RegisterBakedTypes"/> once per process, in the
/// background, after the boot's bake barrier (<see cref="PreWarmCompletion"/>) has settled — any
/// settlement: a completed, faulted or not-applicable bake all mean the compile queue has drained
/// and the adopted bundles have landed, which is all this pass waits for.
///
/// <para>🚨 <b>Never on the readiness path.</b> <see cref="StartAsync"/> returns immediately, the
/// pass is kicked from <c>ApplicationStarted</c>, and nothing gates on it: a replica serves while it
/// runs, exactly as it did before it existed — it only closes, type by type, the gap a replica had
/// for the dynamic types whose instances it never activated
/// (<c>Doc/Architecture/DynamicContentTypeRegistration</c>).</para>
///
/// <para>Registered by <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>,
/// beside the pre-warmer whose barrier it follows. On by default; <see cref="EnabledConfigKey"/>
/// = <c>false</c> is the escape hatch.</para>
/// </summary>
public sealed class DynamicContentTypeRegistrationHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DynamicContentTypeRegistrationHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>Config key that turns the pass off (default: on).</summary>
    public const string EnabledConfigKey = "PreWarm:RegisterContentTypes";

    /// <summary>
    /// Config key overriding the pause between two registrations, as a <see cref="TimeSpan"/>
    /// string. Default <see cref="DynamicContentTypeRegistrar.DefaultBetweenTypes"/>.
    /// </summary>
    public const string BetweenTypesConfigKey = "PreWarm:RegistrationBetweenTypes";

    /// <summary>How many skipped or failed types the summary NAMES before it counts the rest.</summary>
    private const int NamedReported = 25;

    private IDisposable? _startedRegistration;
    private IDisposable? _pass;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedRegistration = lifetime.ApplicationStarted.Register(Kick);
        return Task.CompletedTask;
    }

    private void Kick()
    {
        var configuration = services.GetService<IConfiguration>();
        if (bool.TryParse(configuration?[EnabledConfigKey], out var enabled) && !enabled)
        {
            logger.LogInformation(
                "DynamicContentTypeRegistration: OFF ({Key}=false) — a dynamic NodeType's content type "
                + "registers on this replica only when one of its instances activates here",
                EnabledConfigKey);
            return;
        }

        var pacing = DynamicContentTypeRegistrar.DefaultBetweenTypes;
        var pacingRaw = configuration?[BetweenTypesConfigKey];
        if (!string.IsNullOrWhiteSpace(pacingRaw))
        {
            if (TimeSpan.TryParse(pacingRaw, out var parsed) && parsed >= TimeSpan.Zero)
                pacing = parsed;
            else
                logger.LogWarning(
                    "DynamicContentTypeRegistration: ignoring invalid {Key}='{Value}' — pacing stays {Default}",
                    BetweenTypesConfigKey, pacingRaw, pacing);
        }

        var mesh = services.GetRequiredService<IMessageHub>();
        var bake = services.GetRequiredService<PreWarmCompletion>();
        var startedAt = DateTimeOffset.MinValue;
        // Appended on the pass's own subscription only (Concat — one outcome at a time).
        var outcomes = ImmutableList<ContentTypeRegistrationOutcome>.Empty;

        _pass = bake.Settled
            .Take(1)
            .Do(_ => startedAt = DateTimeOffset.UtcNow)
            .SelectMany(_ => DynamicContentTypeRegistrar.RegisterBakedTypes(mesh, pacing, logger))
            .Subscribe(
                outcome =>
                {
                    outcomes = outcomes.Add(outcome);
                    if (outcome.Status is ContentTypeRegistrationStatus.Registered)
                        logger.LogDebug(
                            "DynamicContentTypeRegistration: registered {TypePath} → {ContentType}",
                            outcome.TypePath, outcome.Detail);
                },
                ex => logger.LogWarning(ex,
                    "DynamicContentTypeRegistration: the pass FAULTED after {Count} type(s) — the rest "
                    + "register only when an instance activates on this replica, as before this pass existed",
                    outcomes.Count),
                () => Summarise(outcomes, DateTimeOffset.UtcNow - startedAt, pacing));
    }

    private void Summarise(
        IReadOnlyList<ContentTypeRegistrationOutcome> outcomes, TimeSpan elapsed, TimeSpan pacing)
    {
        int Count(ContentTypeRegistrationStatus s) => outcomes.Count(o => o.Status == s);
        logger.LogInformation(
            "DynamicContentTypeRegistration: registration-only pass over {Total} dynamic NodeType(s) "
            + "done in {Elapsed} (pacing {Pacing}) — registered={Registered} alreadyRegistered={Already} "
            + "declaresNoContentType={None} notBaked={NotBaked} bytesMissing={Missing} staleBytes={Stale} "
            + "faulted={Faulted}. Nothing was compiled and no NodeType record was written.",
            outcomes.Count, elapsed, pacing,
            Count(ContentTypeRegistrationStatus.Registered),
            Count(ContentTypeRegistrationStatus.AlreadyRegistered),
            Count(ContentTypeRegistrationStatus.DeclaresNoContentType),
            Count(ContentTypeRegistrationStatus.NotBaked),
            Count(ContentTypeRegistrationStatus.BytesMissing),
            Count(ContentTypeRegistrationStatus.StaleBytes),
            Count(ContentTypeRegistrationStatus.Faulted));

        // Named, because a count of types left unregistered is not actionable: these are the types
        // whose content can still render empty on this replica until an instance activates here.
        var unresolved = outcomes
            .Where(o => o.Status is ContentTypeRegistrationStatus.BytesMissing
                or ContentTypeRegistrationStatus.StaleBytes
                or ContentTypeRegistrationStatus.Faulted)
            .ToList();
        if (unresolved.Count == 0)
            return;
        var named = string.Join(", ", unresolved.Take(NamedReported)
            .Select(o => $"{o.TypePath} ({o.Status}: {o.Detail})"));
        if (unresolved.Count > NamedReported)
            named += $", …and {unresolved.Count - NamedReported} more";
        logger.LogWarning(
            "DynamicContentTypeRegistration: {Count} baked dynamic NodeType(s) could NOT be registered on "
            + "this replica — their content stays untyped here until an instance activates here: {Types}",
            unresolved.Count, named);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _startedRegistration?.Dispose();
        _startedRegistration = null;
        _pass?.Dispose();
        _pass = null;
    }
}
