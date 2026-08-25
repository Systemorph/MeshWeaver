using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// WHO a timed publish runs as, and what happens when nobody can be named (issue #50).
///
/// <para>🚨 <b>The defect these pin.</b> The watcher took the scheduling identity from
/// <c>MeshNode.LastModifiedBy</c> on the result of its own live query — and that query is
/// PATH-LESS (<c>nodeType:*Post</c> spans every partition), so Postgres serves it through the
/// cross-schema fan-out <c>public.search_across_schemas</c>, whose record shape is
/// <c>(id, namespace, name, node_type, category, icon, display_order, last_modified, version,
/// state, content, desired_id, main_node)</c>. It carries neither <c>last_modified_by</c> nor
/// <c>created_by</c>, and a <c>select:</c> list cannot conjure a column the fan-out never returns.
/// So every timer armed from a storage read named nobody, and the handler refused it hours later
/// with "it names no CreatedBy" — on memex, on a post that had been approved and scheduled
/// correctly through the page. It looked intermittent only because a timer armed from a LIVE
/// change-feed emission carries the full node and DID have an identity: it worked while someone was
/// watching and failed after every restart.</para>
/// </summary>
public class ScheduledPublishIdentityTest
{
    /// <summary>
    /// A node exactly as the cross-partition fan-out delivers it: content and identity-free
    /// metadata. This IS the production shape, not a degenerate edge case.
    /// </summary>
    private static MeshNode AsDeliveredByTheFanOut() =>
        new("CarsonPublishTest", "Posts")
        {
            NodeType = "SocialMedia/Post",
            State = MeshNodeState.Active,
            Content = System.Text.Json.JsonDocument.Parse(
                """
                {"$type":"SocialPost","text":"hello","authorPath":"Profiles/CarsonLinkedIn",
                 "scheduledAt":"2026-08-23T13:48:00Z","status":"Scheduled"}
                """).RootElement.Clone(),
        };

    /// <summary>The same node read authoritatively BY PATH, which carries every column.</summary>
    private static MeshNode AsReadByPath() =>
        AsDeliveredByTheFanOut() with { CreatedBy = "carson", LastModifiedBy = "carson" };

    /// <summary>
    /// The regression: the query shape names nobody. Pinning it here is what stops the identity
    /// ever being read off a projection again — the field is not merely "sometimes null", it is
    /// null by construction for this query.
    /// </summary>
    [Fact]
    public void TheQueryShapeNamesNobody()
    {
        var post = AsDeliveredByTheFanOut();

        Assert.True(ScheduledPostWatcher.IsSchedulablePost(post),
            "the fan-out shape is a perfectly valid schedulable post — only its identity is missing");
        Assert.Equal("Profiles/CarsonLinkedIn", ScheduledPostWatcher.AuthorPathOf(post));
        Assert.Null(ScheduledPostWatcher.SchedulerIdentity(post));
    }

    /// <summary>The authoritative per-node read is where the identity actually lives.</summary>
    [Fact]
    public void TheAuthoritativeReadNamesTheScheduler()
        => Assert.Equal("carson", ScheduledPostWatcher.SchedulerIdentity(AsReadByPath()));

    /// <summary>
    /// <c>CreatedBy</c> is the fallback for a node the mesh never recorded a modifier for — a post
    /// scheduled by a script, or written before the column was populated.
    /// </summary>
    [Fact]
    public void CreatedByIsTheFallbackWhenNothingModifiedItSince()
    {
        var post = AsDeliveredByTheFanOut() with { CreatedBy = "carson", LastModifiedBy = "   " };
        Assert.Equal("carson", ScheduledPostWatcher.SchedulerIdentity(post));
        Assert.Null(ScheduledPostWatcher.SchedulerIdentity(null));
    }

    /// <summary>
    /// Arming and firing must agree on what a usable identity IS. A timer armed for one the handler
    /// would reject fires and refuses — the post shows as scheduled, the slot passes, nothing
    /// happens. That divergence is the whole bug, so the two ask ONE question.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("system-security")]
    [InlineData("sync/github")]
    public void AnUnusableSchedulerIsRefusedAndSaysWhy(string? scheduler)
    {
        var refusal = ScheduledSocialPublishHandler.UnusableScheduler(scheduler);
        Assert.NotNull(refusal);
        Assert.NotEmpty(refusal!);
    }

    [Fact]
    public void ARealPersonIsUsable()
        => Assert.Null(ScheduledSocialPublishHandler.UnusableScheduler("carson"));

    /// <summary>
    /// The subscription id is derived from the POST PATH and nothing else — that is what makes
    /// re-scheduling MOVE one timer instead of stacking a second one beside it.
    /// </summary>
    [Fact]
    public void TheTimerIdIsDerivedFromThePostPathAlone()
    {
        var id = ScheduledPostWatcher.SubscriptionId("Posts/CarsonPublishTest");
        Assert.Equal(id, ScheduledPostWatcher.SubscriptionId("Posts/CarsonPublishTest"));
        Assert.NotEqual(id, ScheduledPostWatcher.SubscriptionId("Posts/Other"));
        Assert.DoesNotContain('/', id);
    }

    /// <summary>
    /// Every refusal code the publisher can return turns into a sentence the post's author can ACT
    /// on. A wire code (<c>not-connected</c>) on a page is not an explanation.
    /// </summary>
    [Theory]
    [InlineData("not-connected")]
    [InlineData("missing-w_member_social-reconnect")]
    [InlineData("profile-path-missing")]
    [InlineData("empty-text")]
    [InlineData("access-denied")]
    [InlineData("post-not-found")]
    public void EveryRefusalCodeBecomesAReadableSentence(string code)
    {
        var explained = PostPublishProblem.Explain(code);
        Assert.NotEmpty(explained);
        Assert.DoesNotContain(code, explained, StringComparison.Ordinal);
        Assert.EndsWith(".", explained.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>An unrecognised refusal still says something, and still names LinkedIn's status.</summary>
    [Fact]
    public void AnUnknownRefusalStillExplainsItself()
    {
        Assert.Contains("422", PostPublishProblem.Explain("duplicate post", 422), StringComparison.Ordinal);
        Assert.NotEmpty(PostPublishProblem.Explain(null));
    }

    /// <summary>
    /// Recording the reason must never lose the post's own type, and clearing it must actually
    /// clear (a merge that OMITS the key would leave a stale complaint on a post that has since
    /// published).
    /// </summary>
    [Fact]
    public void RecordingTheReasonKeepsThePostTypedAndClearingClears()
    {
        var now = DateTimeOffset.UtcNow;
        var stored = System.Text.Json.JsonDocument.Parse(
            """{"$type":"SocialPost","text":"hello","status":"Scheduled"}""").RootElement.Clone();

        var withProblem = PostPublishProblem.Apply(stored, "It did not go out.", now);
        Assert.Equal("SocialPost", withProblem["$type"]!.GetValue<string>());
        Assert.Equal("It did not go out.", withProblem[PostPublishProblem.ErrorKey]!.GetValue<string>());
        Assert.Equal("hello", withProblem["text"]!.GetValue<string>());

        var cleared = PostPublishProblem.Apply(withProblem, null, now);
        Assert.Equal("SocialPost", cleared["$type"]!.GetValue<string>());
        Assert.True(cleared.ContainsKey(PostPublishProblem.ErrorKey), "the key must be WRITTEN as null, not omitted");
        Assert.Null(cleared[PostPublishProblem.ErrorKey]);
        Assert.Equal("hello", cleared["text"]!.GetValue<string>());
    }
}
