namespace MeshWeaver.Graph.Security;

/// <summary>
/// Tunables for row-level security enforcement. Registered through the ordinary options pipeline
/// (<c>services.Configure&lt;RowLevelSecurityOptions&gt;(...)</c>); every value has a production
/// default, so a host that configures nothing behaves exactly as before.
/// </summary>
public sealed class RowLevelSecurityOptions
{
    /// <summary>
    /// How long <see cref="RlsNodeValidator"/> waits for the effective-permission fold to reach a
    /// verdict before reporting that the check could not be ESTABLISHED (#1446).
    ///
    /// <para>🚨 This is not a ceiling that turns a slow check into a denial — it is what gives the
    /// check a TERMINAL at all. The fold is a <c>CombineLatest</c> over the grant and policy reads
    /// of the target's scope and every ancestor scope; a leg that starves (the cross-silo case,
    /// where the owning activation lives on a peer silo) never emits, never completes and never
    /// errors, so the fold cannot produce any outcome, and the <c>.Take(1)</c> around it bounds the
    /// number of emissions rather than the wait. Past this budget the validator answers
    /// <see cref="Mesh.Services.NodeRejectionReason.Unavailable"/> — neither a grant nor a denial —
    /// so the operation reports its own availability failure instead of sitting until its CALLER
    /// gives up.</para>
    ///
    /// <para>Default 30 s: comfortably above any healthy cold fold (sub-second warm, low seconds
    /// cold), and strictly below the 60 s hub <c>RequestTimeout</c> that would otherwise be the
    /// only thing that ever ended the wait — which is the whole point, because a request killed by
    /// its caller reports the caller's timeout rather than the read that starved.</para>
    /// </summary>
    public TimeSpan PermissionEstablishmentBudget { get; set; } = TimeSpan.FromSeconds(30);
}
