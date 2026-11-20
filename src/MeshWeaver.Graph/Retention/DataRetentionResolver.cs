using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.Graph.Retention;

/// <summary>
/// What a retention rule says about ONE partition: a replacement for any of the three classes, or
/// nothing. A class left null is not spoken for by this rule, so a later rule (and finally the
/// instance policy) decides it. Each class is resolved independently.
/// </summary>
/// <param name="Source">Who said it, for the sweep's log line (e.g. <c>tier:Free</c>).</param>
public sealed record DataRetentionOverride(string Source)
{
    /// <summary>Replacement for the activity class, or null.</summary>
    public ActivityRetention? Activities { get; init; }

    /// <summary>Replacement for the version class, or null.</summary>
    public VersionRetention? Versions { get; init; }

    /// <summary>Replacement for the log-entry class, or null.</summary>
    public LogEntryRetention? LogEntries { get; init; }
}

/// <summary>
/// One link of the per-partition retention chain. A module registers one as a mesh singleton
/// (<c>services.AddSingleton&lt;IDataRetentionRule, …&gt;()</c>) to give a partition windows of
/// its own. The motivating rule is a pricing tier on a multi-tenant instance: the partition's
/// subscription names a tier, and the tier's node DECLARES its retention. Such a rule reads that
/// declaration; it never switches over tier names.
///
/// <para>The chain is OPEN: core ships no rule, any module adds one, and rules are applied in
/// ascending <see cref="Order"/> (then <see cref="Name"/>), never registration order. The first rule
/// that speaks for a class decides it; where none does, the instance policy
/// (<see cref="DataRetentionPolicy"/>, from the deployment record) applies, and where that is not
/// configured, the shipped default.</para>
///
/// <para>🚨 A rule that faults or does not answer within the bound makes the sweep SKIP the
/// partition for that run. It never falls through to the instance policy, because the rule might
/// have kept the data longer (a paid tier) and falling through would delete it early.</para>
/// </summary>
public interface IDataRetentionRule
{
    /// <summary>Stable name, for ordering ties and for the log line.</summary>
    string Name { get; }

    /// <summary>Position in the chain; lower runs first.</summary>
    int Order { get; }

    /// <summary>
    /// What this rule says about <paramref name="partition"/>: an override, or null when it does not
    /// speak for this partition. Cold; emits once.
    /// </summary>
    /// <param name="partition">The partition (first path segment), never stripped or lower-cased.</param>
    IObservable<DataRetentionOverride?> Resolve(string partition);
}

/// <summary>The policy that applies to one partition, with where each class came from.</summary>
/// <param name="Partition">The partition.</param>
/// <param name="Activities">The activity class in force.</param>
/// <param name="ActivitiesSource">Who decided it (<c>instance</c> or a rule's source).</param>
/// <param name="Versions">The version class in force.</param>
/// <param name="VersionsSource">Who decided it.</param>
/// <param name="LogEntries">The log-entry class in force.</param>
/// <param name="LogEntriesSource">Who decided it.</param>
public sealed record ResolvedDataRetention(
    string Partition,
    ActivityRetention Activities, string ActivitiesSource,
    VersionRetention Versions, string VersionsSource,
    LogEntryRetention LogEntries, string LogEntriesSource)
{
    /// <summary>The no-history class in force. Always the instance policy's: no rule speaks for it,
    /// because it is no window a plan could vary — it drains history a type declared worthless.</summary>
    public NoHistoryRetention NoHistory { get; init; } = new();
}

/// <summary>
/// Resolves the retention of one partition: the rule chain first, then the instance policy.
/// A mesh-scoped singleton; holds no state beyond the rules and the policy it was built with.
/// </summary>
public sealed class DataRetentionResolver
{
    /// <summary>The source name of the instance policy in <see cref="ResolvedDataRetention"/>.</summary>
    public const string InstanceSource = "instance";

    /// <summary>How long one rule may take before the partition is skipped for this run.</summary>
    public static readonly TimeSpan RuleBound = TimeSpan.FromSeconds(15);

    private readonly ImmutableArray<IDataRetentionRule> rules;

    /// <summary>Creates the resolver.</summary>
    /// <param name="instance">The instance policy (deployment-record configuration over the shipped default).</param>
    /// <param name="rules">The registered rules, in any order.</param>
    public DataRetentionResolver(DataRetentionPolicy instance, IEnumerable<IDataRetentionRule> rules)
    {
        Instance = instance;
        this.rules = rules
            .OrderBy(r => r.Order)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>The instance policy.</summary>
    public DataRetentionPolicy Instance { get; }

    /// <summary>The rules, in the order they are applied.</summary>
    public ImmutableArray<IDataRetentionRule> Rules => rules;

    /// <summary>
    /// The retention in force for <paramref name="partition"/>. Cold; emits once. Errors when a rule
    /// faults or times out, so the caller skips the partition (see <see cref="IDataRetentionRule"/>).
    /// </summary>
    /// <param name="partition">The partition.</param>
    public IObservable<ResolvedDataRetention> Resolve(string partition)
        => rules.IsEmpty
            ? Observable.Return(Fold(partition, []))
            : rules
                .Select(rule => rule.Resolve(partition)
                    .Take(1)
                    .DefaultIfEmpty(null)
                    .Timeout(RuleBound)
                    .Catch<DataRetentionOverride?, Exception>(ex => Observable.Throw<DataRetentionOverride?>(
                        new InvalidOperationException(
                            $"Retention rule '{rule.Name}' did not answer for partition '{partition}'", ex))))
                .Concat()
                .ToList()
                .Select(overrides => Fold(partition, overrides));

    /// <summary>
    /// Folds the rule answers (in chain order) over the instance policy: the first override that
    /// speaks for a class wins. Then the one cross-class floor: while both are enabled, versions are
    /// kept at least as long as activities, so every retained activity can still be undone (undo
    /// reads the version before the activity's change). Pure.
    /// </summary>
    /// <param name="partition">The partition.</param>
    /// <param name="overrides">The rule answers, in chain order; nulls are rules that did not speak.</param>
    public ResolvedDataRetention Fold(string partition, IEnumerable<DataRetentionOverride?> overrides)
    {
        var answers = overrides.OfType<DataRetentionOverride>().ToImmutableArray();
        var activityAnswer = answers.FirstOrDefault(o => o.Activities is not null);
        var versionAnswer = answers.FirstOrDefault(o => o.Versions is not null);
        var logAnswer = answers.FirstOrDefault(o => o.LogEntries is not null);

        var activities = activityAnswer?.Activities ?? Instance.Activities;
        var versions = versionAnswer?.Versions ?? Instance.Versions;
        if (activities.Enabled && versions.Enabled && versions.MaxAge < activities.MaxAge)
            versions = versions with { MaxAge = activities.MaxAge };

        return new ResolvedDataRetention(
            partition,
            activities, activityAnswer?.Source ?? InstanceSource,
            versions, versionAnswer?.Source ?? InstanceSource,
            logAnswer?.LogEntries ?? Instance.LogEntries, logAnswer?.Source ?? InstanceSource)
        {
            NoHistory = Instance.NoHistory,
        };
    }
}
