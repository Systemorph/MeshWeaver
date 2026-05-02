using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Strongly-typed globals exposed to user scripts compiled by <see cref="KernelContainer"/>.
/// Roslyn's <c>CSharpScript.Create&lt;T&gt;(code, opts, globalsType: typeof(MeshScriptGlobals))</c>
/// makes these properties addressable from script code as bare identifiers — the
/// script body sees <c>Mesh</c> and <c>Log</c> as if they were fields.
/// </summary>
public class MeshScriptGlobals
{
    /// <summary>The current session's <see cref="IMessageHub"/>. Scripts use this to talk to the mesh.</summary>
    public IMessageHub Mesh { get; init; } = default!;

    /// <summary>The script logger. Routes to the script's <c>ActivityLog</c> when one is provided in the request.</summary>
    public ILogger Log { get; init; } = default!;
}
