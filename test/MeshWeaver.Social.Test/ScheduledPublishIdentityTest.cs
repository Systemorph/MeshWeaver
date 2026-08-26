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

        var withProblem = PostPublishProblem.Apply(stored, "not-connected", statusCode: 0, now);
        Assert.Equal("SocialPost", withProblem["$type"]!.GetValue<string>());
        // 🌍 The stored datum is the CODE — that is what a localized renderer will read.
        Assert.Equal("not-connected", withProblem[PostPublishProblem.ErrorCodeKey]!.GetValue<string>());
        // …with the English rendering alongside it, for the bundle page that reads it today.
        Assert.Contains("connect LinkedIn",
            withProblem[PostPublishProblem.ErrorKey]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hello", withProblem["text"]!.GetValue<string>());

        var cleared = PostPublishProblem.Apply(withProblem, null, statusCode: 0, now);
        Assert.Equal("SocialPost", cleared["$type"]!.GetValue<string>());
        Assert.True(cleared.ContainsKey(PostPublishProblem.ErrorKey), "the key must be WRITTEN as null, not omitted");
        Assert.Null(cleared[PostPublishProblem.ErrorKey]);
        Assert.Null(cleared[PostPublishProblem.ErrorCodeKey]);
        Assert.Null(cleared[PostPublishProblem.AttemptedAtKey]);
        Assert.Equal("hello", cleared["text"]!.GetValue<string>());
    }

    /// <summary>
    /// 🚨 <b>The repair-on-sight regression (PR #2261 review).</b> The arming policy used to skip
    /// any Pending timer whose slot already matched — which is EVERY timer the broken build armed:
    /// they carry a null <c>CreatedBy</c> and an otherwise-correct <c>FireAt</c>. So the #50 fix
    /// would have helped only posts scheduled AFTER the deploy, while every already-scheduled post
    /// stayed silently refused at its slot, with the fix in place. A timer this deployment would
    /// refuse to fire must be rewritten, not skipped.
    /// </summary>
    [Fact]
    public void ATimerArmedByTheOldBugIsRepairedRatherThanSkipped()
    {
        var slot = new DateTimeOffset(2026, 8, 23, 13, 48, 0, TimeSpan.Zero);

        // The exact shape the broken build left behind: right slot, nobody to publish as.
        Assert.True(ScheduledPostWatcher.ShouldArm(Timer(slot, EventSubscriptionStatus.Pending, null), slot));
        // A system/hub principal is just as unusable, and just as repairable.
        Assert.True(ScheduledPostWatcher.ShouldArm(
            Timer(slot, EventSubscriptionStatus.Pending, "system-security"), slot));

        // A healthy, unchanged timer is still left completely alone — no churn, no re-emission.
        Assert.False(ScheduledPostWatcher.ShouldArm(
            Timer(slot, EventSubscriptionStatus.Pending, "carson"), slot));

        // A post with no timer at all is armed.
        Assert.True(ScheduledPostWatcher.ShouldArm(null, slot));
        // A re-slotted post moves its timer.
        Assert.True(ScheduledPostWatcher.ShouldArm(
            Timer(slot.AddHours(-1), EventSubscriptionStatus.Pending, "carson"), slot));
    }

    /// <summary>
    /// The repair must NOT loosen the at-most-once rule. Publishing is not idempotent — firing
    /// twice posts to LinkedIn twice — so a handed-over or human-stopped timer is never rewritten,
    /// whatever its identity says. A Failed one is re-armed only for a genuinely different slot,
    /// which ties the retry to a new human decision rather than to a reconcile loop.
    /// </summary>
    [Theory]
    [InlineData(EventSubscriptionStatus.Fired)]
    [InlineData(EventSubscriptionStatus.Cancelled)]
    public void AHandedOverOrStoppedTimerIsNeverRearmed(EventSubscriptionStatus status)
    {
        var slot = new DateTimeOffset(2026, 8, 23, 13, 48, 0, TimeSpan.Zero);

        Assert.False(ScheduledPostWatcher.ShouldArm(Timer(slot, status, "carson"), slot));
        // …not even to "repair" a missing identity, and not even for a new slot.
        Assert.False(ScheduledPostWatcher.ShouldArm(Timer(slot, status, null), slot));
        Assert.False(ScheduledPostWatcher.ShouldArm(Timer(slot, status, "carson"), slot.AddHours(1)));
    }

    [Fact]
    public void AFailedTimerIsRearmedOnlyForADifferentSlot()
    {
        var slot = new DateTimeOffset(2026, 8, 23, 13, 48, 0, TimeSpan.Zero);

        Assert.False(ScheduledPostWatcher.ShouldArm(
            Timer(slot, EventSubscriptionStatus.Failed, "carson"), slot));
        Assert.True(ScheduledPostWatcher.ShouldArm(
            Timer(slot, EventSubscriptionStatus.Failed, "carson"), slot.AddHours(1)));
    }

    /// <summary>A post's publish timer, as the subscription store holds it.</summary>
    private static EventSubscription Timer(
        DateTimeOffset fireAt, EventSubscriptionStatus status, string? createdBy) =>
        new()
        {
            Id = ScheduledPostWatcher.SubscriptionId("Posts/CarsonPublishTest"),
            FireAt = fireAt,
            Status = status,
            CreatedBy = createdBy,
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = "Posts/CarsonPublishTest",
        };

    /// <summary>
    /// 🌍 The recorded problem is a stable CODE plus its status, not only a sentence — that is what
    /// makes it localizable later without re-migrating stored English. The HTTP status is recorded
    /// when LinkedIn answered, and omitted when nothing ever reached the network.
    /// </summary>
    [Fact]
    public void TheProblemIsStoredAsACodeAndAStatus()
    {
        var now = DateTimeOffset.UtcNow;

        var refused = PostPublishProblem.Apply(null, "duplicate post", 422, now);
        Assert.Equal("duplicate post", refused[PostPublishProblem.ErrorCodeKey]!.GetValue<string>());
        Assert.Equal(422, refused[PostPublishProblem.ErrorStatusKey]!.GetValue<int>());

        // A pre-publish gate never reached LinkedIn, so there is no status to record.
        var gated = PostPublishProblem.Apply(null, "not-connected", statusCode: 0, now);
        Assert.Null(gated[PostPublishProblem.ErrorStatusKey]);
    }

    /// <summary>
    /// 🚨 A refusal that names no reason must still RECORD one. Null means CLEAR everywhere in this
    /// class, so passing a null reason straight through would wipe the post's problem and leave the
    /// only person who can act on it seeing nothing at all — the exact silence #50 is about.
    /// </summary>
    [Fact]
    public void ARefusalWithNoReasonStillRecordsAProblem()
    {
        Assert.Equal(PostPublishProblem.UnknownCode, PostPublishProblem.CodeOf(null));
        Assert.Equal(PostPublishProblem.UnknownCode, PostPublishProblem.CodeOf("   "));
        Assert.Equal("not-connected", PostPublishProblem.CodeOf("not-connected"));

        var content = PostPublishProblem.Apply(
            null, PostPublishProblem.CodeOf(null), 500, DateTimeOffset.UtcNow);
        Assert.Equal(PostPublishProblem.UnknownCode,
            content[PostPublishProblem.ErrorCodeKey]!.GetValue<string>());
        Assert.NotEmpty(content[PostPublishProblem.ErrorKey]!.GetValue<string>());
    }

    /// <summary>
    /// 🚨 The regression for the asymmetric no-op (PR #2261 review): a matching CODE is enough to
    /// skip a re-record — that is the write-storm guard — but a CLEAR must look at the whole
    /// problem. A post whose code is already absent can still carry a stale attempted-at written by
    /// an older build, and skipping on the code alone would leave a published post permanently
    /// claiming a failed attempt.
    /// </summary>
    [Fact]
    public void ClearingIsNotSkippedWhileAnyResidueRemains()
    {
        var now = DateTimeOffset.UtcNow;

        // Same code, same status → nothing to write.
        var recorded = PostPublishProblem.Apply(null, "not-connected", statusCode: 0, now);
        Assert.True(PostPublishProblem.AlreadySays(recorded, "not-connected"));
        Assert.False(PostPublishProblem.AlreadySays(recorded, "empty-text"));
        // A DIFFERENT status is a different problem, even under the same code.
        Assert.False(PostPublishProblem.AlreadySays(recorded, "not-connected", 429));

        // Clearing a recorded problem must write.
        Assert.False(PostPublishProblem.AlreadySays(recorded, null));

        // The residue case: no code left, but a stale attempted-at still on the node.
        var residue = System.Text.Json.JsonDocument.Parse(
            "{\"$type\":\"SocialPost\",\"" + PostPublishProblem.AttemptedAtKey
            + "\":\"2026-08-23T13:48:00Z\"}").RootElement.Clone();
        Assert.False(PostPublishProblem.AlreadySays(residue, null));

        // Only a genuinely clean post is a no-op to clear.
        Assert.True(PostPublishProblem.AlreadySays(
            PostPublishProblem.Apply(recorded, null, statusCode: 0, now), null));
    }
}
