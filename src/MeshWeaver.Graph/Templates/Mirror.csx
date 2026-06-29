// Built-in import template — mirrors a MeshNode subtree across portals over
// MCP-HTTP. Triggered via ExecuteScriptRequest with Inputs:
//   remoteBaseUrl:   string (required) — e.g. https://memex.meshweaver.cloud
//   remoteToken:     string (required) — ApiToken issued on the remote portal
//   sourcePath:      string (required) — path on the source side
//   targetPath:      string (optional) — path on the target side (default = sourcePath)
//   direction:       string (optional) — "Push" (default) or "Pull"
//   dryRun:          bool   (optional) — preview without writing
//   removeMissing:   bool   (optional) — destructive — delete target-only nodes
// Returns: MirrorResult (Status, NodesImported, NodesSkipped, Paths…).
using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

string GetReq(string key)
{
    if (!Inputs.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
        throw new InvalidOperationException($"Inputs.{key} is required");
    return el.GetString();
}
string GetOpt(string key) =>
    Inputs.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
        ? el.GetString()
        : null;
bool GetBool(string key) =>
    Inputs.TryGetValue(key, out var el)
    && (el.ValueKind == JsonValueKind.True
        || (el.ValueKind == JsonValueKind.String
            && bool.TryParse(el.GetString(), out var b) && b));

var direction = GetOpt("direction") ?? "Push";
var request = new MirrorRequest
{
    RemoteBaseUrl = GetReq("remoteBaseUrl"),
    RemoteToken = GetReq("remoteToken"),
    SourcePath = GetReq("sourcePath"),
    TargetPath = GetOpt("targetPath"),
    Direction = string.Equals(direction, "Pull", StringComparison.OrdinalIgnoreCase) ? "Pull" : "Push",
    DryRun = GetBool("dryRun"),
    RemoveMissing = GetBool("removeMissing"),
};

Log.LogInformation(
    "Mirror {Direction} {Source} → {Target} on {Url} (dryRun={DryRun}, removeMissing={Remove})",
    request.Direction, request.SourcePath, request.TargetPath ?? request.SourcePath,
    request.RemoteBaseUrl, request.DryRun, request.RemoveMissing);

// Post to the mesh hub — MirrorRequest's handler is registered there
// (AddMirrorHandler) and runs MirrorOperations internally.
var response = await Mesh
    .Observe<MirrorResult>(request, o => o.WithTarget(new Address("mesh")))
    .Take(1)
    .Timeout(TimeSpan.FromMinutes(10))
    .ToTask(Ct);

var result = response.Message;
Log.LogInformation(
    "Mirror finished: status={Status} imported={Imported} skipped={Skipped} removed={Removed} paths={Scanned} ms={Ms}",
    result.Status, result.NodesImported, result.NodesSkipped, result.NodesRemoved,
    result.NodesScanned, result.ElapsedMs);

if (string.Equals(result.Status, "Error", StringComparison.OrdinalIgnoreCase))
    Log.LogError("Mirror error: {Error}", result.Error ?? "(no detail)");

return result;
