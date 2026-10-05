namespace MeshWeaver.Mesh;

/// <summary>
/// How an <see cref="IStaticRepoSource"/>'s partition reconciles the LIVE partition against the
/// shipped source on each import — specifically, what the import PRUNES after upserting the source
/// nodes. This is a <b>per-partition</b> policy, distinct from the <b>per-node</b>
/// <see cref="SyncBehavior"/> (Include / ExcludeThisOnly / ExcludeThisAndChildren), which still
/// applies WITHIN every mode: a node claimed with <see cref="SyncBehavior.ExcludeThisAndChildren"/>
/// (or otherwise non-<see cref="SyncBehavior.Include"/>) is never overwritten or pruned regardless
/// of the partition's mode. See <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public enum PartitionSyncMode
{
    /// <summary>
    /// Default (mirror what the source owns). Upsert every source node, then prune every node the
    /// source PREVIOUSLY owned (recorded in the prior import's manifest) that is now absent from the
    /// current source. Guards still apply (governance <c>_Policy</c>/<c>_Access</c>/<c>_Activity</c>,
    /// claimed subtrees, and non-<see cref="SyncBehavior.Include"/> nodes are never pruned).
    /// 🚨 It no longer prunes an extra the source never put there (policy
    /// <c>prune-requires-provenance</c>): runtime state created in a synced partition survives every
    /// import, so this mode now prunes the same set as <see cref="Additive"/>. Kept as its own value
    /// because it is persisted and the default for any source that does not opt in.
    /// </summary>
    FullReplace = 0,

    /// <summary>
    /// Additive. Upsert every source node, then prune ONLY nodes the source PREVIOUSLY owned (recorded
    /// in the prior import's manifest) that are now absent from the current source. A node a user added
    /// to the partition — never present in any manifest — is NEVER pruned, so user contributions survive
    /// re-import, while a node the repo dropped is still cleaned up. The default for the built-in AI
    /// catalogs (Skill, Agent, Provider, Harness), so users can add their own skills/agents alongside
    /// the shipped ones.
    /// </summary>
    Additive = 1,

    /// <summary>
    /// Upsert-only (no prune). Upsert every source node and NEVER prune anything. The source can only
    /// add/update; nothing in the partition is ever removed by the import (a node the repo removed
    /// lingers until deleted by hand).
    /// </summary>
    UpsertOnly = 2
}
