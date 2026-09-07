using System.Text.RegularExpressions;

namespace MeshWeaver.Mesh;

/// <summary>
/// One module this deployment CARRIES and tried to install, whose registration threw against THIS
/// platform build — so it contributes nothing, and this replica is degraded until the two halves
/// agree again. Registered as an enumerable DI singleton, the same way
/// <see cref="InstalledModuleAssembly"/> records the ones that succeeded.
///
/// <para>🚨 <b>This exists so a version-skewed module cannot take the pod with it (#2234).</b> A
/// landed <c>MeshWeaver.AI.AzureFoundry</c> built against a 9-parameter record ctor met an image
/// carrying the 8-parameter one; its provider attribute called the ctor that did not exist, the
/// <see cref="MissingMethodException"/> escaped <c>MeshBuilder.InstallAssemblies</c>, and the
/// process aborted ~2 s into boot — <c>exit 139</c>, a 178 MB core dump per attempt, and no
/// application logging at all, because the logging pipeline is not up yet at that point. memex-cloud
/// could not start a replacement pod for ~90 minutes and survived only on two pods that had booted
/// before the module landed.</para>
///
/// <para><b>The blast radius was the defect, separately from the binary break.</b> One module's
/// incompatibility is a reason to lose THAT module's contribution, never every other module, the
/// portal, and the ability to roll at all. A deployment whose two halves are consistently old is
/// serving; the same deployment mid-move is a crashloop — which is what strands an install that can
/// move neither half alone.</para>
///
/// <para>🚨 <b>Skipping is NOT quietly tolerating.</b> A skip nobody can see is the shape that
/// forges correct-looking bugs: the portal serves, the feature is simply gone, and nobody files
/// anything because it looks like it works. So every skip is reported three ways — written to
/// stderr at boot (the ONLY channel that exists before logging is configured, and the answer to
/// "the container log contained only the createdump DSO listing"), registered here for any host to
/// surface, and classified <c>RequiredModuleState.Incompatible</c> (PluginCatalog, which
/// depends on this assembly rather than the other way round) so a module
/// declared under <c>Modules:Required</c> is named on <c>/health</c> rather than silently absent.
/// It never reports <c>Present</c>: the assembly did load, so a check that asked only "is it
/// loaded?" would call this healthy, which is the lie this record exists to prevent.</para>
/// </summary>
/// <param name="Entry">The raw <c>Modules:Assemblies</c> entry or resolved path that was installed.</param>
/// <param name="Name">Its assembly simple name — what <c>Modules:Required</c> is matched on.</param>
/// <param name="Error">The exception's type and message, kept whole: for the skew case the message
/// names the exact signature that was missing, which is the sentence an operator acts on.</param>
/// <param name="MissingMember">The member the module wanted and this build does not have, when the
/// exception names one. Null when the failure is of another kind.</param>
public sealed record IncompatibleModule(string Entry, string Name, string Error, string? MissingMember)
{
    /// <summary>
    /// True when the module was refused BEFORE it was ever loaded, because its bytes link against
    /// a platform this deployment is not running (#3538) — as opposed to a module that DID load
    /// and whose registration threw (#2234).
    ///
    /// <para>🚨 <b>The distinction decides whether a ROLLOUT stalls.</b> A module the IMAGE ships
    /// that cannot install is a fault the previous generation does not share and that this
    /// deployment can fix by moving both halves together, so a rollout must stall on it. A
    /// STORE-delivered module built for a platform this one is not running is the declared floor's
    /// case exactly: no rollout of this deployment can conjure that platform, and stalling one
    /// would recreate the 2026-08-22 three-way deadlock in a new place. It is reported and named
    /// (never Healthy), and it starts working by itself at the platform update — which is itself a
    /// restart.</para>
    ///
    /// <para>An INIT property, not a fifth positional parameter: replacing a public record's
    /// constructor signature is what <see cref="MissingMethodException"/>-aborts a host compiled
    /// against the previous platform — which is the very incident this record exists for. Default
    /// <c>false</c> keeps every existing producer's meaning unchanged.</para>
    /// </summary>
    public bool RefusedBeforeLoad { get; init; }

    // MissingMethodException / MissingFieldException render as: Method not found: 'Void Ns.T..ctor(...)'.
    // TypeLoadException names the type instead. Both put the thing that is missing in quotes, which is
    // the one detail that turns "a module failed" into "this build lacks this signature".
    private static readonly Regex QuotedMember = new("'([^']+)'", RegexOptions.Compiled);

    /// <summary>
    /// Builds the record from a LINK verdict — the module was never loaded at all because its
    /// bytes reference types this platform does not carry (#3538).
    ///
    /// <para>🚨 The other half of this record's job, and the half that used to be missing. An
    /// install failure is what happens when the module's registration TOUCHES the missing surface;
    /// a view pack whose <see cref="MeshNodeProviderAttribute"/> touches none of it installs
    /// perfectly and then throws <see cref="TypeLoadException"/> at every render that reaches the
    /// linked type — which is how memex-cloud served dead code cells to every user for a day while
    /// every module reported installed. A module refused HERE is in exactly the same state as one
    /// that failed to install: it contributes nothing, it is reported, and nothing else is
    /// affected.</para>
    ///
    /// <para>🚨 <b>A distinct NAME, deliberately not an overload of <see cref="From"/>.</b> An
    /// added overload is one of the break shapes no gate in this fleet sees: a dependent repo's
    /// <c>&lt;see cref="IncompatibleModule.From"/&gt;</c> becomes ambiguous and fails
    /// <c>CS0419</c> under <c>-warnaserror</c>, in a different repository, on pull requests that
    /// did not make the change (see Doc/Architecture/CrossRepoPairGate). Naming it costs nothing
    /// and removes the shape entirely.</para>
    /// </summary>
    /// <param name="entry">The raw entry or resolved path that was going to be installed.</param>
    /// <param name="verdict">The refusal — <see cref="ModuleLinkState.Unlinkable"/> or
    /// <see cref="ModuleLinkState.Indeterminate"/>.</param>
    public static IncompatibleModule FromLinkRefusal(string entry, ModuleLinkVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return new IncompatibleModule(
            entry,
            Path.GetFileNameWithoutExtension(entry) is { Length: > 0 } name ? name : verdict.Module,
            verdict.Report(),
            verdict.MissingTypes.IsDefaultOrEmpty ? null : verdict.MissingTypes[0])
        {
            RefusedBeforeLoad = true,
        };
    }

    /// <summary>Builds the record from a failed install, extracting the missing member when named.</summary>
    public static IncompatibleModule From(string entry, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var name = Path.GetFileNameWithoutExtension(entry) ?? entry;
        var error = $"{exception.GetType().Name}: {exception.Message}";
        var match = QuotedMember.Match(exception.Message ?? string.Empty);
        return new IncompatibleModule(entry, name, error, match.Success ? match.Groups[1].Value : null);
    }

    /// <summary>
    /// The operator-facing sentence: which module, what it wanted, and what to do about it. Used
    /// for the boot-time stderr line and by any host surfacing this on a health endpoint.
    /// </summary>
    public string Report() =>
        $"Module '{Name}' did not install against this platform build and is CONTRIBUTING NOTHING. "
        + (MissingMember is null
            ? $"{Error}. "
            : $"It requires '{MissingMember}', which this build does not have. ")
        + "This replica is degraded, not healthy: the module's features are absent. Its build and "
        + "the platform's disagree — move BOTH halves together (the module set and the image), "
        + $"never one alone. Entry: {Entry}";
}
