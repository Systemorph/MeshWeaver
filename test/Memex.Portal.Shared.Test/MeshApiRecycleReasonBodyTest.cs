using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Memex.Portal.Shared.Api;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The wire contract of <c>POST /api/mesh/recycle</c>'s body, on both sides of MeshWeaver#4782.
///
/// <para><b>Why it exists.</b> <c>AGENTS.md</c> states carrying a recycle <c>Reason</c> as an
/// absolute, and until #4952 the platform surface could not take one at all. This route and
/// <c>mw recycle</c> are the operator-facing half, so they were where an absolute rule was
/// unsatisfiable. The route now binds <see cref="MeshApiEndpoints.RecycleBody"/> instead of the
/// shared <see cref="MeshApiEndpoints.PathBody"/>.</para>
///
/// <para>🚨 <b>What these cases DO and DO NOT establish</b>, stated because the obvious claim is the
/// wrong one. Swapping the bound record cannot produce a 400 for an older caller: the model binder is
/// <c>System.Text.Json</c>, which fills a missing property with the parameter's default and does not
/// refuse — so "an older body would 400" is not something this file could measure, and an earlier
/// draft of it asserted exactly that and stayed green with <c>Reason</c> made mandatory. What these
/// cases DO pin is the VALUE an absent, a present, and a present-but-blank <c>reason</c> arrives as,
/// which is what the one decider downstream branches on.</para>
///
/// <para>The client side is established by reading rather than by a test, and deliberately:
/// <c>MemexClient</c> constructs its own <c>HttpClient</c>, so there is no seam to stub without
/// reshaping it for a test. <c>MemexJson.Default</c> carries
/// <c>DefaultIgnoreCondition = WhenWritingNull</c>, so <c>mw recycle &lt;path&gt;</c> with no
/// <c>--reason</c> posts <c>{"path":"…"}</c> — byte-identical to what it has always posted — and the
/// first case below is that body.</para>
///
/// <para>Asserted over the binding rather than over HTTP because the route is on the Bearer policy:
/// an end-to-end test would need the whole token pipeline and a live mesh to prove something about a
/// record's shape. What the <c>Reason</c> then becomes inside the <c>DisposeRequest</c> is pinned by
/// <c>RecycleReasonTest</c> over <c>MeshOperations.RecycleReason</c>; this file covers the seam
/// between the two, and neither covers the three-line lambda that hands one to the other.</para>
/// </summary>
public class MeshApiRecycleReasonBodyTest
{
    // The endpoint's own binder: System.Text.Json with web defaults, i.e. camelCase property names.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The body every caller before #4782 sent, and the one <c>mw recycle</c> without <c>--reason</c>
    /// still sends. It must arrive with <c>Reason</c> NULL — not <c>""</c> — because null is the value
    /// that selects the framework's self-describing fallback, and a blank is the one thing worse than
    /// no reason at all (it looks like a caller who answered). Falsified by giving <c>Reason</c> any
    /// non-null default.
    /// </summary>
    [Fact]
    public void A_body_carrying_only_a_path_binds_with_no_reason_stated()
    {
        var body = JsonSerializer.Deserialize<MeshApiEndpoints.RecycleBody>(
            """{"path":"Doc/Architecture/AccessControl"}""", Web);

        body.Should().NotBeNull();
        body!.Path.Should().Be("Doc/Architecture/AccessControl");
        body.Reason.Should().BeNull();
    }

    /// <summary>
    /// The case that could not be expressed before #4782. Falsified by renaming or dropping the
    /// property, which is the way this route's half of the change actually breaks.
    /// </summary>
    [Fact]
    public void A_body_carrying_a_reason_binds_it()
    {
        var body = JsonSerializer.Deserialize<MeshApiEndpoints.RecycleBody>(
            """{"path":"Store/Plugin","reason":"re-binding after the 9077 roll"}""", Web);

        body.Should().NotBeNull();
        body!.Path.Should().Be("Store/Plugin");
        body.Reason.Should().Be("re-binding after the 9077 roll");
    }

    /// <summary>
    /// 🚨 A reason that is PRESENT and BLANK is delivered verbatim, not normalised here.
    ///
    /// <para>The one assertion in this file about a decision rather than a shape. A script or a UI
    /// sending an untouched text box supplies <c>""</c> or whitespace, and those are refused by
    /// <c>MeshOperations.RecycleReason</c>, which is parameterised over <c>null</c>, <c>""</c> and
    /// <c>"   "</c> in <c>RecycleReasonTest</c>. Trimming or nulling them HERE would give the codebase
    /// two places that decide what a blank reason means, and the one that is wrong would be the one
    /// nobody reads. So the binder stays faithful and the decision stays in one place.</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_present_but_blank_reason_is_delivered_verbatim_for_one_decider_downstream(string blank)
    {
        var json = JsonSerializer.Serialize(new { path = "Store/Plugin", reason = blank });

        var body = JsonSerializer.Deserialize<MeshApiEndpoints.RecycleBody>(json, Web);

        body.Should().NotBeNull();
        body!.Reason.Should().Be(blank);
    }

    /// <summary>
    /// 🚨 THE ROUTE ITSELF FORWARDS THE REASON — asserted over the SOURCE, because nothing else here
    /// can see it (review finding on #5154).
    ///
    /// <para>Every other case in this file deserializes <see cref="MeshApiEndpoints.RecycleBody"/> and
    /// stops there. <c>MapMeshApi</c>'s route lambda is never executed, so the two regressions that
    /// would actually break this feature's REST half — reverting the bound type to
    /// <see cref="MeshApiEndpoints.PathBody"/>, or leaving <c>RecycleBody</c> in place while the lambda
    /// calls <c>ops.Recycle(body.Path)</c> and drops the reason — would both leave those cases
    /// green. Acknowledging that in a doc comment, which the first version of this file did, is not
    /// covering it.</para>
    ///
    /// <para><b>Why a source assertion and not an adapter seam.</b> The alternative the finding offers
    /// is to extract the forwarding into a named function a test can invoke. That catches a change to
    /// the extracted function and NOT a route rewritten to bypass it — which is the more likely of the
    /// two regressions, because bypassing an indirection is what someone simplifying the route would
    /// do. A source assertion catches both named regressions directly, and adds no production
    /// indirection whose only purpose is to be observed. Executing the route for real is the third
    /// option and is out of proportion: the route is on the Bearer policy and <c>RunString</c>
    /// constructs a <c>MeshOperations</c> over a session hub, so it would need the whole token pipeline
    /// and a live mesh to prove that one argument is passed.</para>
    ///
    /// <para><b>What it does NOT establish:</b> that the reason reaches the target's
    /// <c>DisposeRequest</c>. That is <c>RecycleReasonTest</c>'s subject, over
    /// <c>MeshOperations.RecycleReason</c>. This case covers the one link between them that no
    /// behavioural test in either project sees.</para>
    /// </summary>
    [Fact]
    public void The_recycle_route_binds_RecycleBody_and_forwards_its_Reason()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "memex", "Memex.Portal.Shared", "Api", "MeshApiEndpoints.cs"));

        // The mapping and its lambda, from `MapPost("/recycle"` to the `));` that closes the call —
        // matched tolerantly so reformatting the argument list across lines cannot silently pass.
        var mapping = Regex.Match(source, @"MapPost\(""/recycle"".*?\)\);", RegexOptions.Singleline);

        mapping.Success.Should().BeTrue("POST /api/mesh/recycle must still be mapped at all");

        mapping.Value.Should().Contain("RecycleBody",
            "the route must bind RecycleBody; PathBody carries no Reason, so reverting to it silently "
            + "removes the operator's why from the REST surface");
        mapping.Value.Should().Contain("body.Reason",
            "the route must FORWARD the reason — binding a body that carries one and then calling "
            + "ops.Recycle(body.Path) is the regression this case exists for, and it breaks nothing "
            + "else in this file");
    }

    /// <summary>
    /// The routes that deliberately did NOT gain a reason. <c>/compile</c> and <c>/diagnostics</c>
    /// bind <see cref="MeshApiEndpoints.PathBody"/>, and a field that means nothing on two of three
    /// routes is a field callers guess about — which is why the swap was to a NEW record rather than a
    /// widened shared one. Asserted because widening the shared body is the cheaper edit and the one a
    /// later change is likely to reach for.
    /// </summary>
    [Fact]
    public void The_shared_PathBody_gains_nothing_so_compile_and_diagnostics_are_unchanged()
    {
        var names = typeof(MeshApiEndpoints.PathBody).GetProperties()
            .Select(p => p.Name)
            .ToArray();

        names.Should().Equal(["Path"],
            "/compile and /diagnostics bind PathBody and have no use for a recycle reason");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
