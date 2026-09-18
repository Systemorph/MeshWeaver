using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>AN UNCHANGED NODETYPE DOES NOT MAKE THE PROPOSAL THE TYPE AUTHORITY</b> — issue #4597,
/// the third face of the contract <c>UpdateValidatorSeesTypedExistingContentTest</c> (#3056) and
/// <c>UpdateRetypeExistingContentTest</c> (#3803) pin between them.
///
/// <para><c>NodeUpdatePipeline.WithExistingContentTyped</c> types the EXISTING snapshot as the
/// PROPOSED content's CLR type. #3803 gated that on the NodeType not changing — but the NodeType
/// string is a PROXY for the real precondition, which is that the two sides carry the same content
/// RECORD, and the proxy comes apart whenever a writer proposes a different record under an
/// unchanged (or omitted) NodeType. Production does that routinely, and #4597 is the measurement:
/// <c>PartnerRe/Esl/EmailDraft-DueDiligence-2026-09-05</c>, a node whose stored content is the
/// Essentials/Email plugin's <c>EmailContent</c>, updated with a <c>MarkdownContent</c>.</para>
///
/// <para><b>The harm, and it is silent.</b> <see cref="JsonSerializer"/> deserialising into a
/// concrete type discards unmapped members and defaults the rest, so the stored bytes bind CLEANLY
/// into the proposed record whenever its members are defaultable — and validators receive a
/// well-formed instance of a state the node was never in. That is #1379's lesson, which
/// <c>IMeshContentTypeRegistry.TryRecoverForNodeType</c> and <c>ContentSchemaValidator</c> both
/// already apply through <see cref="Mesh.Services.ContentDiscriminator"/> and this seam did
/// not.</para>
///
/// <para><b>What the incident actually showed</b> is the other half of the same defect: where the
/// conversion CANNOT happen, <c>ObjectAsExtensions.As</c> reports it as a recovery failure at
/// <see cref="LogLevel.Error"/> — <c>"value is EmailContent (DynamicNode_Essentials_Email), not
/// convertible"</c>. Nothing was broken: the update proceeded and the snapshot was correctly left
/// alone. Asking a question the seam has no business asking, and then filing the answer as a fault,
/// is what turned a legitimate write into four incidents in twelve days.</para>
///
/// <para>🚨 The counterparty tests are load-bearing. The gate must NOT swallow the #3056 cure
/// (a same-short-named record from another collectible assembly must still be recovered) nor the
/// discriminator-less recovery (bytes that name no type contradict nothing, so they are admitted).
/// A fix that skipped the recovery on every update would pass the defect tests and fail every one
/// of these.</para>
///
/// <para>🚨 Both JSON shapes are covered, and that is not symmetry for its own sake.
/// <c>MeshNode.Content</c> takes three shapes — a live instance, a <see cref="JsonElement"/> read
/// from storage, and the as-written <see cref="System.Text.Json.Nodes.JsonObject"/> DOM — and the
/// DOM reaches its own <c>Deserialize</c> branch. A guard tested on <see cref="JsonElement"/> alone
/// would leave the reshaping live on the shape nothing measured (Copilot review, #4679).</para>
///
/// <para><b>No mocking.</b> The conversion is measured through the REAL hub's
/// <see cref="JsonSerializerOptions"/> — the seam takes them as a parameter for exactly that
/// reason. The recorder below is an <see cref="ILogger"/> implementation, not a substitute for a
/// core interface: the diagnostic IS the subject of two of these tests, and the same recorder shape
/// is what <c>ObjectAsPrivatePayloadTest</c> uses on the production accessor.</para>
/// </summary>
public class NodeUpdateContentTypeChangeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string NodeTypePath = "Probe/EmailLike";
    private const string NodePath = "probe/EmailDraft";

    /// <summary>Stands in for the plugin-compiled record the node's NodeType declares.</summary>
    /// <param name="To">Free-form.</param>
    /// <param name="Subject">Free-form.</param>
    public record ProbeEmailContent(string To, string Subject);

    /// <summary>
    /// Stands in for the framework record a markdown-shaped writer proposes. Every member is
    /// DEFAULTABLE on purpose: that is what lets the stored bytes bind into it silently, which is
    /// the manufacture under test. A record with a <c>required</c> member (the real
    /// <c>MarkdownContent</c>) throws instead and only produces the noisy half of the defect.
    /// </summary>
    /// <param name="Content">Free-form.</param>
    /// <param name="Subject">Shared with <see cref="ProbeEmailContent"/>, so a bind carries a real
    /// stored value across and the ghost looks plausible rather than empty.</param>
    public record ProbeMarkdownContent(string? Content = null, string? Subject = null);

    private static MeshNode Proposed(object content) => MeshNode.FromPath(NodePath) with
    {
        Name = "Edited",
        NodeType = NodeTypePath,
        Content = content,
    };

    private static MeshNode Stored(object content) => MeshNode.FromPath(NodePath) with
    {
        Name = "Original",
        NodeType = NodeTypePath,
        Content = content,
    };

    private static JsonElement StoredEmailBytes(string discriminator) =>
        JsonDocument.Parse(
            $$"""{"$type":"{{discriminator}}","to":"counterparty@example.com","subject":"Due diligence"}""")
            .RootElement.Clone();

    /// <summary>
    /// 🚨 THE DEFECT. Stored bytes that NAME THEIR OWN RECORD must never be re-bound into a
    /// differently-named record just because the proposal carries one and the NodeType did not
    /// change. The bind SUCCEEDS here — that is the point — so before the fix the validator's
    /// "existing" content was a <see cref="ProbeMarkdownContent"/> carrying the email's subject and
    /// a null body: a state the node was never in.
    /// </summary>
    [Fact]
    public void ContentWhoseDiscriminatorNamesAnotherRecord_IsNotReboundIntoTheProposedOne()
    {
        var stored = StoredEmailBytes(nameof(ProbeEmailContent));
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new ProbeMarkdownContent("# Edited")),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeOfType<JsonElement>(
            "the stored bytes declare themselves a ProbeEmailContent, so the proposal is NOT their "
            + "type authority. System.Text.Json binds them into ProbeMarkdownContent CLEANLY "
            + "(unmapped members dropped, the rest defaulted), so a seam that converts anyway hands "
            + "the Update validators a well-formed instance of a state the node was never in — "
            + "#1379's manufacture, arriving through an unchanged NodeType instead of a retype");
        ((JsonElement)result.Content!).GetRawText().Should().Be(stored.GetRawText(),
            "and leaving the snapshot alone means leaving it EXACTLY as it was");
    }

    /// <summary>
    /// 🚨 THE REPORTED SYMPTOM. A live instance of another record is the same fact stated by a
    /// value that already bound, and <c>As</c> correctly refuses to convert it — but it reports
    /// that refusal as a RECOVERY FAILURE at Error, which is what filed #4597. Nothing failed:
    /// there are two content types here and the seam has no business converting between them.
    /// </summary>
    [Fact]
    public void ATypedSnapshotOfAnotherRecord_IsNotReportedAsAFailedRecovery()
    {
        var stored = new ProbeEmailContent("counterparty@example.com", "Due diligence");
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new ProbeMarkdownContent("# Edited")),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeSameAs(stored, "the node's own content is what a validator must see");

        logger.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty(
            "nothing failed. The update is legitimate and proceeds, and the snapshot is correctly "
            + "left alone — but As reported the refused conversion as 'not convertible' at Error, "
            + "so a plugin-typed node saved by a markdown-shaped writer filed an incident every "
            + "time (#4597: four occurrences in twelve days, all benign). Captured: {0}",
            string.Join(" | ", logger.Entries.Select(e => $"{e.Level}: {e.Text}")));

        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning,
            "the fact IS worth a line: the validators are deciding on a snapshot typed differently "
            + "from the proposal, which is the one state in which a typed comparison silently skips")
            .Subject;
        warning.Text.Should().Contain(nameof(ProbeMarkdownContent))
            .And.Contain(nameof(ProbeEmailContent),
                "a diagnostic that names neither content type cannot be acted on");
        warning.Text.Should().NotContain("counterparty@example.com",
            "content never reaches a log — only type names (ObjectAsExtensions.LogRecoveryFailure)");
    }

    /// <summary>
    /// 🚨 COUNTERPARTY ONE — the #3056 cure the gate must not swallow. A record with the SAME short
    /// name from another assembly is the copy every NodeType recompile mints, and recovering it is
    /// precisely what <c>UpdateValidatorSeesTypedExistingContentTest</c> pins. Here the stored
    /// bytes carry that short name, so the discriminator ADMITS the proposal and the conversion
    /// must still happen.
    /// </summary>
    [Fact]
    public void ContentWhoseDiscriminatorNamesTheProposedRecord_IsStillTyped()
    {
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new ProbeMarkdownContent("# Edited")),
            Stored(StoredEmailBytes(nameof(ProbeMarkdownContent))),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeOfType<ProbeMarkdownContent>(
            "the stored bytes name the SAME record the proposal carries — a rebuild into a new "
            + "collectible assembly, or another package's copy — so the proposal IS the type "
            + "authority and the #3056 recovery must still run. A fix that skipped the conversion "
            + "on every update would pass the two tests above and fail here");
        ((ProbeMarkdownContent)result.Content!).Subject.Should().Be("Due diligence",
            "and the recovery must carry the node's real stored values across, not defaults");
    }

    /// <summary>
    /// 🚨 THE DEFECT, IN THE OTHER JSON SHAPE. <c>MeshNode.Content</c> takes three shapes, and the
    /// as-written <see cref="JsonObject"/> DOM — content a writer built as a node rather than
    /// parsed from storage — is one of the three <c>ObjectAsExtensions</c> names. It reaches
    /// <c>JsonNode.Deserialize</c> by its own branch, so a guard that covered only
    /// <see cref="JsonElement"/> would leave the reshaping live on the shape nothing tested
    /// (Copilot review, #4679).
    /// </summary>
    [Fact]
    public void DomContentWhoseDiscriminatorNamesAnotherRecord_IsNotReboundIntoTheProposedOne()
    {
        var stored = JsonNode.Parse(
            $$"""{"$type":"{{nameof(ProbeEmailContent)}}","to":"counterparty@example.com","subject":"Due diligence"}""")!;
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new ProbeMarkdownContent("# Edited")),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeSameAs(stored,
            "the DOM shape carries a $type exactly as stored bytes do, and it deserialises into the "
            + "proposed record just as silently — so the guard has to read it, not only JsonElement");
        logger.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty(
            "nothing failed here either. Captured: {0}",
            string.Join(" | ", logger.Entries.Select(e => $"{e.Level}: {e.Text}")));
    }

    /// <summary>
    /// 🚨 ITS COUNTERPARTY — the DOM shape must still convert when its own <c>$type</c> names the
    /// proposed record, for the same reason <see cref="ContentWhoseDiscriminatorNamesTheProposedRecord_IsStillTyped"/>
    /// gives: narrowing the recovery must not take the #3056 cure with it.
    /// </summary>
    [Fact]
    public void DomContentWhoseDiscriminatorNamesTheProposedRecord_IsStillTyped()
    {
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new ProbeMarkdownContent("# Edited")),
            Stored(JsonNode.Parse(
                $$"""{"$type":"{{nameof(ProbeMarkdownContent)}}","subject":"Due diligence"}""")!),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeOfType<ProbeMarkdownContent>(
            "an admitted DOM payload takes the same recovery an admitted JsonElement does");
        ((ProbeMarkdownContent)result.Content!).Subject.Should().Be("Due diligence",
            "and carries the node's real stored values across, not defaults");
    }

    /// <summary>
    /// 🚨 COUNTERPARTY TWO — absent is not contradicting. Bytes that name no type disagree with
    /// nothing, and the proposal is then the only evidence available; refusing here would break
    /// every legitimate discriminator-less recovery the seam exists for.
    /// </summary>
    [Fact]
    public void ContentWithNoDiscriminator_IsStillTypedByTheProposal()
    {
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new ProbeMarkdownContent("# Edited")),
            Stored(JsonDocument.Parse("""{"subject":"Due diligence"}""").RootElement.Clone()),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeOfType<ProbeMarkdownContent>(
            "content written without a $type has nothing to contradict the proposal with, so it is "
            + "admitted — the same rule IMeshContentTypeRegistry.TryRecoverForNodeType applies");
    }

    /// <summary>
    /// Captures what the seam logged, so the diagnostic can be asserted on rather than inferred
    /// from a code read. Same shape as <c>ObjectAsPrivatePayloadTest.CapturingLogger</c>.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        /// <summary>
        /// 🚨 Immutable, reassigned per record — the collections policy holds in <c>test/</c> too
        /// (Copilot review, #4679). The seam under test is a pure function called on the test
        /// thread, so there is no concurrent writer to serialise; what the policy buys here is that
        /// a captured <see cref="Entries"/> cannot be mutated behind an assertion's back.
        /// </summary>
        public ImmutableList<(LogLevel Level, string Text, Exception? Exception)> Entries { get; private set; } =
            ImmutableList<(LogLevel, string, Exception?)>.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries = Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
