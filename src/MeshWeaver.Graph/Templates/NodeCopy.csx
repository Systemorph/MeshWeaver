// Built-in import template — copies a MeshNode subtree (root + descendants)
// to a target namespace. Triggered via ExecuteScriptRequest with Inputs:
//   sourcePath:      string  (required) — path of the source root node
//   targetNamespace: string  (required) — namespace under which the copy lands
//                                         (use "" for root)
//   force:           bool    (optional) — overwrite existing target nodes
// Returns: { sourcePath, targetNamespace, count, force }.
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

if (!Inputs.TryGetValue("sourcePath", out var srcEl) || srcEl.ValueKind != JsonValueKind.String)
    throw new InvalidOperationException("Inputs.sourcePath is required");
var sourcePath = srcEl.GetString();

var targetNamespace = Inputs.TryGetValue("targetNamespace", out var tgtEl) && tgtEl.ValueKind == JsonValueKind.String
    ? tgtEl.GetString() ?? ""
    : "";

var force = Inputs.TryGetValue("force", out var forceEl)
            && (forceEl.ValueKind == JsonValueKind.True
                || (forceEl.ValueKind == JsonValueKind.String
                    && bool.TryParse(forceEl.GetString(), out var b) && b));

Log.LogInformation(
    "Copying node tree from {Source} to namespace {Target} (force={Force})",
    sourcePath, string.IsNullOrEmpty(targetNamespace) ? "<root>" : targetNamespace, force);

var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

var count = await NodeCopyHelper
    .CopyNodeTree(meshService, meshService, Mesh, sourcePath, targetNamespace, force)
    .FirstAsync()
    .ToTask(Ct);

Log.LogInformation("Copy complete: {Count} node(s)", count);

// Return a JSON object the dispatch handler can deserialize. Use a plain
// dictionary rather than an anonymous type to avoid any serializer surprises
// across hub boundaries (compiler-generated record types don't have stable
// names registered on the mesh-wide TypeRegistry).
return new Dictionary<string, object?>
{
    ["sourcePath"] = sourcePath,
    ["targetNamespace"] = targetNamespace,
    ["count"] = count,
    ["force"] = force,
};
