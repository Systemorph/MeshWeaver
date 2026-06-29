using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// MeshNode-aware <see cref="IStreamOwnerResolver"/>: reads the node OWNER (<c>CreatedBy</c>) from the
/// <see cref="MeshNode"/> carried in a synchronization stream's current value. Registered on every
/// per-node hub via <c>ConfigureDefaultNodeHub</c>, so
/// <see cref="SynchronizationStream{TStream}.Update(System.Func{TStream, TStream}, System.Action{System.Exception})"/> can inject the owner on the cold-start FIRST
/// write — before the owning hub has established its standing identity asynchronously. The node is
/// already in the stream's <c>Current</c> at write time, so this is race-free.
/// </summary>
internal sealed class MeshNodeStreamOwnerResolver : IStreamOwnerResolver
{
    public AccessContext? ResolveOwner(object? currentValue, Address nodePath)
    {
        var path = nodePath?.ToString();
        if (string.IsNullOrEmpty(path))
            return null;

        var nodes = ExtractNodes(currentValue);
        if (nodes is null || nodes.Count == 0)
            return null;

        // Prefer the node whose Path matches the stream's owner; fall back to the sole MeshNode
        // present (a per-node data-source store typically carries exactly the one node).
        var node = nodes.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))
                   ?? (nodes.Count == 1 ? nodes[0] : null);

        var owner = node?.CreatedBy ?? node?.LastModifiedBy;
        // Real-user filtering is applied by the caller (SynchronizationStream.Update via IsRealUser),
        // so a hub/system principal can never leak into CreatedBy here.
        return string.IsNullOrEmpty(owner)
            ? null
            : new AccessContext { ObjectId = owner, Name = owner };
    }

    private static IReadOnlyList<MeshNode>? ExtractNodes(object? value) => value switch
    {
        MeshNode mn => [mn],
        InstanceCollection ic => ic.Instances.Values.OfType<MeshNode>().ToList(),
        // Scan EVERY collection — the MeshNode collection name is registry-driven (not literally
        // "MeshNode"), and typed nodes (Thread, …) round-trip as MeshNode instances too.
        EntityStore store => store.Collections.Values
            .SelectMany(c => c.Instances.Values.OfType<MeshNode>()).ToList(),
        _ => null
    };
}
