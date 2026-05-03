using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Strongly-typed globals exposed to user scripts compiled by <see cref="KernelContainer"/>.
/// Roslyn's <c>CSharpScript.Create&lt;T&gt;(code, opts, globalsType: typeof(MeshScriptGlobals))</c>
/// makes these properties addressable from script code as bare identifiers — the
/// script body sees <c>Mesh</c>, <c>Log</c>, <c>Ct</c>, and <c>Inputs</c> as if they were fields.
/// </summary>
public class MeshScriptGlobals
{
    /// <summary>The current session's <see cref="IMessageHub"/>. Scripts use this to talk to the mesh.</summary>
    public IMessageHub Mesh { get; init; } = default!;

    /// <summary>The script logger. Routes to the script's <c>ActivityLog</c> when one is provided in the request.</summary>
    public ILogger Log { get; init; } = default!;

    /// <summary>
    /// The script's <see cref="CancellationToken"/>. Pass to any cancellable
    /// async API (<c>Task.Delay(ms, Ct)</c>, <c>HttpClient.GetAsync(url, Ct)</c>,
    /// reactive <c>FirstAsync(predicate).ToTask(Ct)</c>) so a user-initiated
    /// cancel via the Activity Control Plane (<c>RequestedStatus = Cancelled</c>)
    /// actually interrupts long-running work mid-flight. Without it, the script
    /// only checks for cancellation at <c>await</c> resume points — fine for
    /// short awaits, useless for a 30-second <c>Task.Delay</c>.
    ///
    /// <para>Settable (not <c>init</c>) so the executor can rebind it per
    /// submission — the globals object is shared across REPL submissions but
    /// each submission gets its own cancellation source.</para>
    /// </summary>
    public CancellationToken Ct { get; set; } = default;

    /// <summary>
    /// Caller-supplied input payload, forwarded from
    /// <c>ExecuteScriptRequest.Inputs</c> via <c>SubmitCodeRequest.Inputs</c>.
    /// Empty dictionary when the request did not carry inputs (the common
    /// REPL/launch-button case). Read with e.g.
    /// <c>Inputs["title"].GetString()</c> or
    /// <c>Inputs["options"].Deserialize&lt;ExportOptions&gt;()</c>.
    ///
    /// <para>Settable (not <c>init</c>) for the same reason as <see cref="Ct"/>:
    /// the globals object is shared across REPL submissions; the executor
    /// rebinds it per <c>SubmitCodeRequest</c>.</para>
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Inputs { get; set; } =
        ImmutableDictionary<string, JsonElement>.Empty;
}
