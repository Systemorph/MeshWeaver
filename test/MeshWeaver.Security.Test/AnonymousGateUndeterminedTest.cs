using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// 🚨 <b>Issue #2901 — the anonymous gate had TWO answers for THREE facts.</b>
///
/// <para><b>What stood here.</b>
/// <c>AnonymousGate.AllowAnonymous</c> was
/// <c>hub.CheckPermission(path, Anonymous, Read).Catch&lt;bool, Exception&gt;(_ =&gt; Observable.Return(false))</c>
/// — the exact shape <c>HubPermissionExtensions</c> forbids in its own doc. A permission fold that
/// FAULTED, or that terminated without ever emitting (#2742), became a bare <c>false</c>,
/// byte-identical to "we read the grants and you are not on the list". The visitor was redirected
/// to <c>/login</c>, the content route answered 404, and the degraded dependency left no trace at
/// all: no log line, nothing retryable, nothing to grep.</para>
///
/// <para><b>The three states this pins, and why the direction is not symmetric.</b> This gate
/// decides what an ANONYMOUS visitor may see, so the two wrong answers are wrong in different
/// ways:
/// <list type="bullet">
///   <item><description>Undetermined must NEVER read as GRANTED — that would serve private content
///     to the public. <see cref="PermissionCheckOutcome.IsGranted"/> is <c>false</c> on that leg,
///     so even a consumer that ignores the tri-state fails CLOSED. Pinned by
///     <see cref="AnUndeterminedFold_DoesNotGrantAnonymousAccess"/> — the anti-widening
///     control.</description></item>
///   <item><description>Undetermined must ALSO never read as DENIED — that is the bug. Pinned by
///     <see cref="AFaultedFold_IsUndetermined_NotADenial"/> and
///     <see cref="ASilentFold_IsUndetermined_NotADenial"/>.</description></item>
///   <item><description>A GENUINE denial must still be a denial — otherwise the fix would just
///     have moved the lie one state over, and every gated page would answer "temporarily
///     unavailable" forever. Pinned by <see cref="AGenuineDenial_IsStillADefinitiveDenial"/>, with
///     <see cref="AGenuineGrant_IsStillGranted"/> as the over-reach control on the same
///     mesh.</description></item>
/// </list></para>
///
/// <para><b>Determinism.</b> The two degraded paths get their answer from an injected
/// <see cref="EffectivePermissionsDelegate"/> that faults / completes empty synchronously at
/// subscribe time, so neither can race a warm cached query. Every OTHER path — including the
/// granted and denied controls — still goes through the REAL
/// <c>PermissionEvaluator</c> over real <c>AccessAssignment</c> nodes, so the controls
/// remain integration tests and not a restatement of the stub.</para>
/// </summary>
public class AnonymousGateUndeterminedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A fold that FAULTS — a storage hiccup, a hub mid-recycle, a cross-silo reply lost.</summary>
    private const string FaultingPath = "AnonFold/Faulting";

    /// <summary>
    /// A fold that COMPLETES WITHOUT EMITTING — issue #2742's terminal. One silent leg of the
    /// evaluator's <c>CombineLatest</c> empties the whole fold, and from outside it is
    /// indistinguishable from this.
    /// </summary>
    private const string SilentPath = "AnonFold/Silent";

    /// <summary>Really public: an explicit positive Anonymous Viewer grant on the root.</summary>
    private const string PublicPath = "PublicCourse";

    /// <summary>Really not public: no anonymous grant anywhere on its scope chain.</summary>
    private const string PrivatePath = "PrivateSpace/Node";

    private const string FaultMarker = "the permission fold faulted";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", PublicPath,
                    accessObject: WellKnownUsers.Anonymous))
            // AnonymousGate reads the evaluator off the hub it is handed (here: the mesh hub), so
            // the degradation is injected there. Chained AFTER ConfigureMeshBase's
            // AddRowLevelSecurity, and MeshBuilder applies hub configurations in order, so this
            // Set wins — and `real` is the evaluator that call installed, i.e. the REAL
            // PermissionEvaluator. Everything that is not one of the two synthetic paths is
            // delegated straight back to it, which is what keeps the granted / denied controls
            // honest integration assertions rather than a restatement of this stub.
            .ConfigureHub(c =>
            {
                var real = c.Get<EffectivePermissionsDelegate>()
                           ?? throw new InvalidOperationException(
                               "AddRowLevelSecurity must have installed the real evaluator before "
                               + "this override — without it the controls below would assert nothing");
                return c.WithPermissionEvaluator((hub, path, userId) => path switch
                {
                    FaultingPath => Observable.Throw<Permission>(new InvalidOperationException(FaultMarker)),
                    SilentPath => Observable.Empty<Permission>(),
                    _ => real(hub, path, userId)
                });
            });

    // Security tests need granular permissions — skip the PublicAdmin seed.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// 🚨 THE REGRESSION PIN. Before the fix the only answer available here was <c>false</c>, and
    /// the caller had no way to tell it from a real denial — so it redirected to <c>/login</c>.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AFaultedFold_IsUndetermined_NotADenial()
    {
        var outcome = await AnonymousGate.Evaluate(Mesh, FaultingPath)
            .Should().Emit("the gate produces exactly one outcome, always — an empty gate stream "
                           + "is read as 'nothing objected' by every consumer");

        outcome.IsUndetermined.Should().BeTrue(
            "a fold that faulted reached NO verdict — reporting it as a denial tells a visitor who "
            + "may be entitled that the page is not for them, and hides a degraded dependency "
            + "behind a routine-looking /login bounce");
        outcome.UndeterminedReason.Should().Contain(FaultMarker,
            "the caller and the operator both need the reason to reach them; the swallow this "
            + "replaced discarded it entirely");
    }

    /// <summary>#2742's terminal, reached through the anonymous gate rather than the message gate.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ASilentFold_IsUndetermined_NotADenial()
    {
        var outcome = await AnonymousGate.Evaluate(Mesh, SilentPath)
            .Should().Emit("a fold that completes without emitting must still produce an outcome — "
                           + "silence is not consent");

        outcome.IsUndetermined.Should().BeTrue(
            "no verdict was produced, so there is nothing to report as a denial");
    }

    /// <summary>
    /// 🚨 THE ANTI-WIDENING CONTROL. The whole point of the tri-state is that "we could not find
    /// out" is not a denial — the failure mode a fix like this invites is making it a GRANT
    /// instead, which would serve private content to the internet. Both degraded paths, both
    /// surfaces (the tri-state AND the boolean projection every existing caller still uses).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnUndeterminedFold_DoesNotGrantAnonymousAccess()
    {
        foreach (var path in new[] { FaultingPath, SilentPath })
        {
            var outcome = await AnonymousGate.Evaluate(Mesh, path).Should().Emit("the gate answers");
            outcome.IsGranted.Should().BeFalse(
                $"an undetermined fold on '{path}' must never widen what an anonymous visitor sees "
                + "— IsGranted is false on that leg precisely so a consumer that ignores the "
                + "tri-state still fails CLOSED");

            await AnonymousGate.AllowAnonymous(Mesh, path)
                .Should().Match(allowed => !allowed,
                    $"the boolean projection of an undetermined fold on '{path}' is false, not "
                    + "true — every caller that has not yet moved to Evaluate stays fail-closed");
        }
    }

    /// <summary>
    /// A REAL denial, from the REAL evaluator, must stay a denial — definitive, not "unavailable".
    /// If this flipped to undetermined, every gated page would start answering "temporarily
    /// unavailable" and retrying forever, which is the same lie pointed the other way.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AGenuineDenial_IsStillADefinitiveDenial()
    {
        var outcome = await AnonymousGate.Evaluate(Mesh, PrivatePath)
            .Should().Emit("the gate answers for an ordinary private node");

        outcome.IsGranted.Should().BeFalse("no anonymous grant covers this path");
        outcome.IsUndetermined.Should().BeFalse(
            "the grants were read and they do not cover this node — that IS a verdict, and the "
            + "caller may act on it (redirect to /login) and cache it");
    }

    /// <summary>The over-reach control: a fail-closed fix that refuses everything would pass every
    /// assertion above and still be broken.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AGenuineGrant_IsStillGranted()
    {
        var outcome = await AnonymousGate.Evaluate(Mesh, PublicPath)
            .Should().Emit("the gate answers for a node carrying an explicit Anonymous grant");

        outcome.IsGranted.Should().BeTrue(
            "an explicit positive Anonymous Read grant is what the public course cover / catalog "
            + "is published with");
        outcome.IsUndetermined.Should().BeFalse("the fold reached a verdict");
    }
}
