// The control record's node-pool HEADROOM (Memex#863; Doc/Architecture/DeploymentAKS ("A spare slot per node pool")). Init-only
// properties only: a new positional constructor parameter would replace the record's signature and
// break every module compiled against the old one (the binary-break rule).
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace MeshWeaver.Deployment;

/// <summary>
/// One node pool's spare SLOT — a low-priority placeholder the chart keeps scheduled on
/// <see cref="Pool"/>, sized like the largest pod that must always find room there (a portal's surge
/// pod). A surge pod preempts the placeholder and schedules at once; the evicted placeholder goes
/// Pending, and the cluster autoscaler adds a node for it while the pool is below its maximum. So a
/// surge-first roll (<c>maxSurge: 1</c> / <c>maxUnavailable: 0</c>) always finds one slot, instead of
/// deadlocking on a full pool (Memex#863). The cost is the slot itself: it stays idle.
///
/// <para>Declared on the CONTROL record only — the placeholder is cluster-scoped, like a cluster
/// manifest, and one per pool is the point: declared on every instance it would be one per instance.</para>
/// </summary>
public sealed record NodeHeadroomSlot
{
    /// <summary>The replicas of a slot with none stated.</summary>
    public const int DefaultReplicas = 1;

    /// <summary>The AKS node pool's name (its <c>kubernetes.azure.com/agentpool</c> label), e.g. <c>silos</c>.</summary>
    [Description("Node pool name (its agentpool label), e.g. silos")]
    public string Pool { get; init; } = "";

    /// <summary>The CPU the slot holds — a Kubernetes quantity (<c>4</c>, <c>3500m</c>); requests equal limits.</summary>
    [Description("CPU the spare slot holds (a Kubernetes quantity, e.g. 4)")]
    public string Cpu { get; init; } = "";

    /// <summary>The memory the slot holds — a Kubernetes quantity (<c>8Gi</c>); requests equal limits.</summary>
    [Description("Memory the spare slot holds (a Kubernetes quantity, e.g. 8Gi)")]
    public string Memory { get; init; } = "";

    /// <summary>How many such slots the pool keeps; at least one.</summary>
    [Description("How many spare slots of this size the pool keeps (at least 1)")]
    public int Replicas { get; init; } = DefaultReplicas;

    private static readonly Regex PoolName = new("^[a-z][a-z0-9]{0,11}$", RegexOptions.CultureInvariant);
    private static readonly Regex CpuQuantity = new(@"^(?:\d+(?:\.\d+)?|\d+m)$", RegexOptions.CultureInvariant);
    private static readonly Regex MemoryQuantity = new(@"^\d+(?:\.\d+)?(?:Ki|Mi|Gi|Ti|k|M|G|T)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every reason <paramref name="slots"/> cannot render: a pool name that is not an AKS pool name, a pool
    /// declared twice, a CPU or memory that is not a Kubernetes quantity (or is zero), fewer than one replica.
    /// Empty when every slot renders. Pure.
    /// </summary>
    public static ImmutableList<string> ProblemsOf(IEnumerable<NodeHeadroomSlot?>? slots)
    {
        var problems = ImmutableList.CreateBuilder<string>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var (slot, i) in (slots ?? []).Select((s, i) => (s, i)))
        {
            if (slot is null)
            {
                problems.Add($"nodeHeadroom[{i}] is empty");
                continue;
            }
            var pool = (slot.Pool ?? "").Trim();
            if (!PoolName.IsMatch(pool))
                problems.Add($"nodeHeadroom[{i}].pool '{pool}' is not a node pool name (lowercase letters and digits, starting with a letter, at most 12)");
            else if (!seen.Add(pool))
                problems.Add($"nodeHeadroom[{i}].pool '{pool}' is declared twice — one entry per pool, with its replicas");
            var cpu = (slot.Cpu ?? "").Trim();
            if (!CpuQuantity.IsMatch(cpu) || IsZero(cpu))
                problems.Add($"nodeHeadroom[{i}].cpu '{cpu}' is not a positive Kubernetes CPU quantity (e.g. 4 or 3500m)");
            var memory = (slot.Memory ?? "").Trim();
            if (!MemoryQuantity.IsMatch(memory) || IsZero(memory))
                problems.Add($"nodeHeadroom[{i}].memory '{memory}' is not a positive Kubernetes memory quantity (e.g. 8Gi)");
            if (slot.Replicas < 1)
                problems.Add($"nodeHeadroom[{i}].replicas {slot.Replicas} is below 1 — remove the entry to keep no slot");
        }
        return problems.ToImmutable();
    }

    private static bool IsZero(string quantity) =>
        !quantity.Any(c => c is >= '1' and <= '9');
}
