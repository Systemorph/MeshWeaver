using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Fixture;
using MeshWeaver.Markdown;
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
/// <c>Globex/Team/EmailDraft-DueDiligence-2026-09-05</c>, a node whose stored content is the
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 🚨 #5736 / #4597's surviving half: "As<MarkdownContent> for {path} could not recover value:
    // JsonException". Measured through the REAL MarkdownContent (its one `required` member is what
    // makes the bind THROW instead of defaulting), because that is the record production proposed.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private const string StoredMarkdownText = "# Konzeptpapier — private body";

    /// <summary>
    /// 🚨 #5736. Content stored WITHOUT a <c>$type</c> in a shape <c>MarkdownContent</c> cannot
    /// bind — the legacy <c>{"markdown": …}</c> key (<c>MarkdownOverviewLayoutArea.ReadMarkdownContent</c>
    /// calls it Unreadable, #4600) — makes no claim to BE a <c>MarkdownContent</c>. Its failure to
    /// bind is therefore not a failed recovery, and the update that replaces it (the repair) must not
    /// file an incident. Both JSON shapes, for the reason the class doc gives.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndiscriminatedLegacyMarkdownKey_IsNotReportedAsAFailedRecovery(bool asDom)
    {
        var json = $$"""{"markdown":"{{StoredMarkdownText}}"}""";
        object stored = asDom
            ? JsonNode.Parse(json) ?? throw new InvalidOperationException("a JSON object literal parses to a node")
            : JsonDocument.Parse(json).RootElement.Clone();
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new MarkdownContent { Content = "# Edited" }),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeSameAs(stored,
            "bytes that do not bind are left exactly as they are — never replaced by a guess");
        AssertReportedAsWarningNotError(logger, "a JSON object");
    }

    /// <summary>
    /// 🚨 A bare JSON STRING is a SANCTIONED markdown shape — <c>WithMarkdownContent</c> keeps "a
    /// plain string a string" and <c>ReadMarkdownContent</c> reads it as Present — so a markdown
    /// writer saving over it is the most ordinary update there is. <c>MarkdownContent</c> is an
    /// object, so the bind throws every time, and before the fix every such save logged Error.
    /// </summary>
    [Fact]
    public void BareStringMarkdown_IsNotReportedAsAFailedRecovery()
    {
        var stored = JsonDocument.Parse($"\"{StoredMarkdownText}\"").RootElement.Clone();
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new MarkdownContent { Content = "# Edited" }),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeOfType<JsonElement>()
            .Which.GetRawText().Should().Be(stored.GetRawText());
        AssertReportedAsWarningNotError(logger, "a JSON string");
    }

    /// <summary>
    /// 🚨 THE COUNTERPARTY that keeps the fix from being a log-level band-aid. Content that NAMES
    /// the proposed record and still will not bind is genuinely corrupt — the writer that stored it
    /// is the defect (#4600/#4657) — and that stays at Error.
    /// </summary>
    [Fact]
    public void DiscriminatedContentThatDoesNotBind_IsStillAnError()
    {
        var logger = new RecordingLogger();

        NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new MarkdownContent { Content = "# Edited" }),
            Stored(JsonDocument.Parse("""{"$type":"MarkdownContent","content":42}""").RootElement.Clone()),
            GetHost().JsonSerializerOptions,
            logger);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error,
            "a payload that CLAIMS to be a MarkdownContent and is not one is real corruption");
    }

    /// <summary>
    /// 🚨 #5736 / #6639 — a <c>MarkdownContent</c> payload that carries NO <c>content</c> member and
    /// nothing but members the record declares is a document with no body yet. While
    /// <c>Content</c> was <c>required</c> its bind threw, and because the payload names its own
    /// record the update of the node logged <c>As&lt;MarkdownContent&gt; for … could not recover
    /// value: JsonException</c> at Error and handed the validators an untyped snapshot. It must
    /// type as an EMPTY document, keep its other members, and log nothing. Both JSON shapes.
    /// </summary>
    /// <param name="json">The stored payload.</param>
    /// <param name="asDom">Whether it is stored as the as-written DOM rather than a JsonElement.</param>
    [Theory]
    [InlineData("""{"$type":"MarkdownContent"}""", false)]
    [InlineData("""{"$type":"MarkdownContent"}""", true)]
    [InlineData("""{"$type":"MarkdownContent","tags":["draft"],"abstract":"Summary"}""", false)]
    [InlineData("""{"$type":"MarkdownContent","tags":["draft"],"abstract":"Summary"}""", true)]
    [InlineData("""{"tags":["draft"],"abstract":"Summary"}""", false)]
    public void MarkdownWithoutContentMember_IsTypedAsAnEmptyDocument(string json, bool asDom)
    {
        object stored = asDom
            ? JsonNode.Parse(json) ?? throw new InvalidOperationException("a JSON object literal parses to a node")
            : JsonDocument.Parse(json).RootElement.Clone();
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new MarkdownContent { Content = "# Edited" }),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        var typed = result.Content.Should().BeOfType<MarkdownContent>(
            "a payload with no body and only declared members IS a MarkdownContent — an empty one").Subject;
        typed.Content.Should().BeEmpty("an absent body is the empty body");
        if (json.Contains("tags"))
        {
            typed.Tags.Should().Equal("draft");
            typed.Abstract.Should().Be("Summary", "the members that were there are carried across");
        }
        (typed.UnknownMembers?.Count ?? 0).Should().Be(0, "the discriminator is metadata, never a stray member");
        logger.Entries.Should().BeEmpty(
            "nothing failed: the stored snapshot binds. Captured: {0}",
            string.Join(" | ", logger.Entries.Select(e => $"{e.Level}: {e.Text}")));
    }

    /// <summary>
    /// 🚨 THE COUNTERPARTY that keeps the empty default from HIDING a body. A payload that names
    /// <c>MarkdownContent</c>, has no <c>content</c>, and carries its text under a member the record
    /// does not declare (the measured <c>body</c> / <c>markdown</c> / <c>text</c> rows of #6639) must
    /// NOT bind as an empty document — that is #4600's "No content yet" invitation over a full
    /// document. It stays a refused bind, at Error (it claims the record), naming the node's path
    /// and never its text.
    /// </summary>
    /// <param name="member">The undeclared member the body was stored under.</param>
    [Theory]
    [InlineData("body")]
    [InlineData("markdown")]
    [InlineData("text")]
    public void MarkdownWithItsBodyUnderAnUndeclaredMember_IsNotBoundAsEmpty(string member)
    {
        var stored = JsonDocument.Parse(
            $$"""{"$type":"MarkdownContent","{{member}}":"{{StoredMarkdownText}}"}""").RootElement.Clone();
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new MarkdownContent { Content = "# Edited" }),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeOfType<JsonElement>(
            "binding an empty MarkdownContent over a body stored under another key would hide it")
            .Which.GetRawText().Should().Be(stored.GetRawText());
        var error = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        error.Text.Should().Contain(NodePath, "the line must name the node a repair has to visit");
        error.Text.Should().NotContain(StoredMarkdownText, "content never reaches a log");
    }

    /// <summary>
    /// 🚨 COUNTERPARTY — undiscriminated bytes that DO bind are still typed by the proposal
    /// (the #3056 cure), with the real record and its real stored value.
    /// </summary>
    [Fact]
    public void UndiscriminatedMarkdownThatBinds_IsStillTyped()
    {
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new MarkdownContent { Content = "# Edited" }),
            Stored(JsonDocument.Parse($$"""{"content":"{{StoredMarkdownText}}"}""").RootElement.Clone()),
            GetHost().JsonSerializerOptions,
            logger);

        result.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be(StoredMarkdownText);
        logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// 🚨 #4597's titled shape through the REAL <c>MarkdownContent</c>: a plugin record (live, or
    /// stored with its own <c>$type</c>) saved over by a markdown-shaped writer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PluginContentProposedAsMarkdown_IsNotReportedAsAFailedRecovery(bool asJson)
    {
        object stored = asJson
            ? StoredEmailBytes("EmailContent")
            : new ProbeEmailContent("counterparty@example.com", "Due diligence");
        var logger = new RecordingLogger();

        var result = NodeUpdatePipeline.WithExistingContentTyped(
            Proposed(new MarkdownContent { Content = "# Edited" }),
            Stored(stored),
            GetHost().JsonSerializerOptions,
            logger);

        if (asJson)
            result.Content.Should().BeOfType<JsonElement>()
                .Which.GetRawText().Should().Be(((JsonElement)stored).GetRawText());
        else
            result.Content.Should().BeSameAs(stored);
        logger.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty(
            "two content types, nothing to convert, nothing failed. Captured: {0}",
            string.Join(" | ", logger.Entries.Select(e => $"{e.Level}: {e.Text}")));
    }

    private static void AssertReportedAsWarningNotError(RecordingLogger logger, string shape)
    {
        logger.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty(
            "the stored content never claimed to be a MarkdownContent, so its failure to bind is "
            + "an answer to a question the seam asked, not a fault — #5736 / #4597 filed it as "
            + "'As<MarkdownContent> for … could not recover value: JsonException'. Captured: {0}",
            string.Join(" | ", logger.Entries.Select(e => $"{e.Level}: {e.Text}")));
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning,
            "the validators DO see an untyped snapshot, and that is worth one line").Subject;
        warning.Text.Should().Contain(NodePath).And.Contain("MarkdownContent").And.Contain(shape);
        warning.Text.Should().NotContain(StoredMarkdownText, "content never reaches a log");
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
