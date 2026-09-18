#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>THE ADMIN'S CHOICE MUST REACH THE RECORD — #3542, the half that outlived both earlier
/// fixes.</b>
///
/// <para><b>What #3607 and #3619 each settled, and what neither touched.</b> #3607 made an ABSENT
/// <c>policy</c> field read as <see cref="UpdatePolicyKind.None"/> rather than as "enabled", by
/// renaming <c>UpdatePolicyContent.Policy</c> to <c>DeclaredPolicy</c> and giving it
/// <c>[JsonPropertyName("policy")]</c>. #3619 stopped a bookkeeping write that could not READ the
/// record from overwriting it with a default. Between them, an install that has lost its policy
/// fails closed and stops losing it again — and the ONE surface built to put the policy back, the
/// Updates settings tab, silently stopped working in the very commit that renamed the property.</para>
///
/// <para><b>The mechanism.</b> <see cref="MeshNodeEditorField.FromType"/> derived each editor
/// field's JSON <c>Key</c> from the CLR PROPERTY NAME (<c>p.Name.ToCamelCase()</c>), ignoring
/// <c>[JsonPropertyName]</c>. <c>MeshNodeContentEditorView</c> uses that key verbatim, on both
/// sides: <c>LoadValues</c> reads <c>obj[f.Key]</c> and <c>Persist</c> writes <c>obj[f.Key]</c>. So
/// after the rename the "Update strategy" dropdown bound to <c>declaredPolicy</c> — a key the
/// record neither writes nor reads:</para>
/// <list type="bullet">
///   <item><description>an install WITH a policy renders the dropdown EMPTY (the read misses);</description></item>
///   <item><description>an admin who then picks one persists <c>declaredPolicy</c>, which
///     deserializes into nothing — <c>Policy</c> stays <see cref="UpdatePolicyKind.None"/>.
///     🚨 Measured here: the value does not even reach storage. The owning hub materialises the
///     content as <c>UpdatePolicyContent</c> and re-serialises it, so the unknown key is dropped on
///     that round trip — <see cref="WaitForWrittenValue"/> spends its whole budget without ever
///     seeing <c>Stable</c> on the record. The control keeps the choice in its own field state, so
///     the tab shows it until the next emission and then silently reverts to unset.</description></item>
/// </list>
///
/// <para>🚨 <b>That is what makes the fault self-latching rather than merely wrong.</b> Under
/// <c>None</c> the poller records "updates are disabled on this install" and evaluates nothing
/// (#3795), so no check, hold or verdict ever contradicts the tab. The single repair surface is a
/// no-op that looks like a success, and nothing else on the install disagrees with it.</para>
///
/// <para>Measured on memex.meshweaver.cloud, 2026-09-18: <c>Admin/UpdatePolicy</c> carries no
/// <c>policy</c> field, its <c>lastCheckVerdict</c> reads "updates are disabled on this install
/// (Admin/UpdatePolicy = None); the registry was not listed", and its <c>heldAt</c> has been frozen
/// at 2026-09-11T08:57Z ever since — the timestamp of the last evaluation that ran while a policy
/// was still on the record.</para>
///
/// <para>🚨 <b>The writes here are the view's, byte for byte</b> — the same
/// <c>obj[key] = value</c> read-modify-write through <c>GetMeshNodeStream(path).Update(...)</c> that
/// <c>MeshNodeContentEditorView.Persist</c> posts (the shape
/// <c>MeshWeaver.GitSync.Test.MeshNodeContentEditorTest</c> already pins for the sync config). The
/// Blazor view lives in MeshWeaver.Plugins and is unchanged by this fix: it uses
/// <see cref="MeshNodeEditorField.Key"/> exactly as declared, and the key is what was wrong.</para>
/// </summary>
public class AdminChosenPolicyReachesTheRecordTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>An install in the state memex-cloud is in: updates disabled, real bookkeeping on the
    /// record. The admin's repair is to pick a strategy on the Updates tab.</summary>
    private const string DisabledRecord =
        """{"$type":"UpdatePolicyContent","policy":"None","requireCiGreen":true,"latestAvailableTag":"3.0.0-ci.8009"}""";

    /// <summary>🚨 <see cref="TestTimeouts.Convergence"/>, never a literal: a hand-written 30 s is
    /// both a guess about machine speed AND the framework's own write bound (#2819).</summary>
    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// The dropdown an admin actually reaches for: the field carrying
    /// <c>UpdatePolicyContent.DeclaredPolicy</c>'s own <c>[Description]</c> — the only one of the
    /// two the tab rendered with a label (and a German <c>[Translation]</c>) that says what it is.
    /// Selected by that label rather than by key ON PURPOSE: the key is the subject under test, so
    /// naming it here would make the assertion circular.
    /// </summary>
    private static MeshNodeEditorField StrategyField() =>
        MeshNodeEditorField.FromType(typeof(UpdatePolicyContent))
            .Single(f => f.Label == "Update strategy");

    /// <summary>
    /// 🚨 The SECOND half of the same reflection defect: <c>[JsonIgnore]</c> was not a reason to
    /// skip a property, so the computed <c>UpdatePolicyContent.Policy</c> — a read-side convenience
    /// that exists only to fold an absent declaration to <see cref="UpdatePolicyKind.None"/> — was
    /// offered as a second, unlabelled strategy dropdown. Two dropdowns over the same enum, one of
    /// which is read and one of which is not, and nothing on the tab says which.
    /// </summary>
    [Fact]
    public void TheUpdatesTabOffersExactlyOneStrategyDropdown()
    {
        var strategy = MeshNodeEditorField.FromType(typeof(UpdatePolicyContent))
            .Where(f => f.Kind == MeshNodeEditorFieldKind.Enum
                        && f.Options.SequenceEqual(Enum.GetNames<UpdatePolicyKind>()))
            .Select(f => $"{f.Key} ('{f.Label}')")
            .ToList();

        Output.WriteLine($"strategy dropdowns: {string.Join(", ", strategy)}");

        strategy.Should().HaveCount(1,
            "a [JsonIgnore] property is one the record does not persist, so offering it as an "
            + "editable field puts a second control over the same value on the tab — and an admin "
            + "has no way to tell which of the two is the one that is read (#3542)");
    }

    /// <summary>
    /// 🚨 THE REGRESSION. An admin picks <c>Stable</c> in the Updates tab's strategy dropdown; the
    /// install must then FOLLOW <c>Stable</c>. Before the fix the write landed under
    /// <c>declaredPolicy</c>, nothing read it, and <c>Policy</c> stayed <c>None</c> — the update
    /// stayed disabled while the tab showed the choice as applied.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task AnAdminWhoPicksAStrategyOnTheUpdatesTabChangesThePolicy()
    {
        await Seed(DisabledRecord, TestContext.Current.CancellationToken);

        var field = StrategyField();
        Output.WriteLine($"strategy field key = '{field.Key}', label = '{field.Label}'");

        await PersistAsTheEditorDoes(field.Key, JsonValue.Create(nameof(UpdatePolicyKind.Stable)));

        var content = await WaitForWrittenValue(nameof(UpdatePolicyKind.Stable));

        content.Policy.Should().Be(UpdatePolicyKind.Stable,
            "the strategy an admin picks on the Updates tab is the ONLY surface built to re-enable "
            + "an install whose policy was lost — a write that lands under a key nothing reads "
            + "leaves updates disabled while the tab reports success, and under None nothing ever "
            + "evaluates to contradict it (#3542)");

        content.LatestAvailableTag.Should().Be("3.0.0-ci.8009",
            "a per-field editor write must not replace the record it patched");
    }

    /// <summary>
    /// 🚨 The READ half of the same key. An install that HAS a policy must render it in the
    /// dropdown; a key that misses reads as unset, which is what invites the admin to "set" a
    /// policy that is already there and then discard it.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task TheStrategyDropdownShowsThePolicyTheRecordCarries()
    {
        await Seed(DisabledRecord, TestContext.Current.CancellationToken);

        var field = StrategyField();

        // MeshNodeContentEditorView.LoadValues, byte for byte: obj[f.Key] off the node stream.
        var shown = (await AsEditorJsonObject())?[field.Key]?.ToString();
        Output.WriteLine($"dropdown would show: '{shown ?? "<unset>"}'");

        shown.Should().Be(nameof(UpdatePolicyKind.None),
            "the Updates tab reads the record through this key; when it misses, an install with an "
            + "explicit policy renders as if it had none");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. A field with neither trap — its CLR name already equals its JSON
    /// name, and its value is not a CLR default — must land through the very same write path, and
    /// must do so BEFORE the fix as well as after. Without it, "the edit did not stick" would be
    /// satisfied by an editor write that simply does not work, and by a harness that never wrote
    /// anything. (Measured before the fix: this arm is the one that passed.)
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task AFieldWithNeitherTrapLands()
    {
        await Seed(DisabledRecord, TestContext.Current.CancellationToken);

        var pattern = MeshNodeEditorField.FromType(typeof(UpdatePolicyContent))
            .Single(f => f.Key == "pattern");

        await PersistAsTheEditorDoes(pattern.Key, JsonValue.Create("3.0.1-ci*"));

        var content = await WaitForContent(c => c.Pattern is not null);

        content.Pattern.Should().Be("3.0.1-ci*");
        content.LatestAvailableTag.Should().Be("3.0.0-ci.8009",
            "a per-field editor write leaves the bookkeeping the poller owns alone");
    }

    /// <summary>
    /// 🚨 THE THIRD WAY THE SAME TAB DISCARDS A CHOICE, and an independent defect from the key:
    /// <see cref="UpdatePolicyContent.RequireCiGreen"/> defaults to <c>true</c> through a property
    /// INITIALIZER, while the hub serializer omits <c>WhenWritingDefault</c> — and <c>false</c> IS
    /// the CLR default for a bool. So unticking "Only update to CI-verified (green) builds" wrote a
    /// value the serializer dropped, and the next read re-applied the initializer's <c>true</c>.
    ///
    /// <para>The cure is the one <c>NotificationSettings</c> and <c>GitHubSyncConfig</c> already
    /// document on every default-true bool they own: <c>[JsonIgnore(Condition = Never)]</c>. This
    /// was the ONLY editable property across the five editor-bound content types in the tree whose
    /// initializer differs from its CLR default and which lacked the attribute — every
    /// <c>HomeConfig</c> and <c>GitHubSyncConfig</c> enum defaults to its own zero member, so their
    /// round trip was already lossless.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task UntickingOnlyUpdateToCiVerifiedBuildsSticks()
    {
        await Seed(DisabledRecord, TestContext.Current.CancellationToken);

        var green = MeshNodeEditorField.FromType(typeof(UpdatePolicyContent))
            .Single(f => f.Kind == MeshNodeEditorFieldKind.Bool);
        green.Key.Should().Be("requireCiGreen");

        await PersistAsTheEditorDoes(green.Key, JsonValue.Create(false));

        var content = await WaitForContent(c => !c.RequireCiGreen);

        content.RequireCiGreen.Should().BeFalse(
            "a checkbox an admin can untick and that never stays unticked is a setting the platform "
            + "silently overrules (#3542)");
        content.Policy.Should().Be(UpdatePolicyKind.None,
            "a per-field editor write touches only its own field");
    }

    /// <summary>
    /// <c>MeshNodeContentEditorView.Persist</c>, reproduced exactly: read the node's content as a
    /// <see cref="JsonObject"/>, set ONE key, write the whole object back through the node stream.
    /// Nothing here is a stand-in — the key is what the backend declared and the write is the one
    /// the GUI posts.
    /// </summary>
    private Task PersistAsTheEditorDoes(string key, JsonNode? value) =>
        Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Update(node =>
                        {
                            var obj = ToJsonObject(node.Content) ?? new JsonObject();
                            obj[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
                            return node with
                            {
                                Content = JsonSerializer.SerializeToElement<object>(
                                    obj, Mesh.JsonSerializerOptions),
                            };
                        })
                        .Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>The node content in the shape the editor view reads its values from.</summary>
    private Task<JsonObject?> AsEditorJsonObject() =>
        RawContent()
            .Select(ToJsonObject)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    private JsonObject? ToJsonObject(object? content) =>
        content switch
        {
            null => null,
            JsonElement je => JsonNode.Parse(je.GetRawText()) as JsonObject,
            _ => JsonSerializer.SerializeToNode(content, Mesh.JsonSerializerOptions) as JsonObject,
        };

    /// <summary>
    /// Waits for the editor's write to be OBSERVABLE on the record — the VALUE it wrote, under
    /// whatever key it wrote it — and only then parses and asserts.
    ///
    /// <para>🚨 The predicate may NOT be <c>DeclaredPolicy is not null</c>, which is what this test
    /// asked for first. The seeded record already carries an explicit <c>policy: "None"</c>, so that
    /// is TRUE of the PRE-WRITE snapshot: the wait returned the state the write had not touched yet
    /// and the assertion ran against something it never observed. It passed locally and failed on
    /// CI (shard 5, run 13126) — a real ordering race, not a flake, and the same trap
    /// <see cref="UnreadablePolicyRecordIsNotClobberedTest"/> documents on its own positive
    /// controls.</para>
    ///
    /// <para>Filtering on the written VALUE is what makes the test decisive, and it turned out to be
    /// a SHARPER statement of the defect than intended: before the fix this wait spends its entire
    /// budget and times out, because <c>Stable</c> never appears on the record under ANY key. The
    /// owning hub materialises the content as <c>UpdatePolicyContent</c> and re-serialises it, so
    /// <c>declaredPolicy</c> is dropped on that round trip. The admin's choice is not merely read by
    /// nobody — it is never stored. A timeout is the honest verdict for "the write had no effect
    /// this test could observe", and it is the same shape (and cost) as the negative arm of
    /// <see cref="UntickingOnlyUpdateToCiVerifiedBuildsSticks"/>.</para>
    /// </summary>
    private Task<UpdatePolicyContent> WaitForWrittenValue(string jsonValue) =>
        RawContent()
            .Where(c => RawJson(c).Contains(jsonValue, StringComparison.Ordinal))
            .Select(c => UpdatePolicyNodeType.ParseContent(c, Mesh.JsonSerializerOptions))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>The stored bytes, not a parse of them — "the write is on the record" is a fact about
    /// what is ON the node, and the parser fails closed to <c>None</c> for a record that has no
    /// policy at all.</summary>
    private string RawJson(object? content) => content switch
    {
        null => string.Empty,
        JsonElement je => je.GetRawText(),
        _ => JsonSerializer.Serialize(content, Mesh.JsonSerializerOptions),
    };

    private Task<UpdatePolicyContent> WaitForContent(Func<UpdatePolicyContent, bool> settled) =>
        RawContent()
            .Select(c => UpdatePolicyNodeType.ParseContent(c, Mesh.JsonSerializerOptions))
            .Where(settled)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    private IObservable<object?> RawContent() =>
        Observable.Create<object?>(observer =>
        {
            using (Access.ImpersonateAsSystem())
                return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                    .Where(n => n is not null)
                    .Select(n => (object?)n.Content)
                    .Subscribe(observer);
        });

    private Task Seed(string json, CancellationToken cancellationToken)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = JsonDocument.Parse(json).RootElement.Clone(),
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(cancellationToken);
    }
}
