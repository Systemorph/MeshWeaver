#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="InstanceSecrets"/>, the secrets a global administrator enters in the portal
/// (Doc/Architecture/InstanceSecrets), and the control inbox's use of them as per-sender keys
/// (Doc/Architecture/SelfUpdateAnnouncementKey):
/// <list type="bullet">
/// <item>a saved key is stored ENCRYPTED, never in the clear, and is never written to a log;</item>
/// <item>it is used LIVE, with no restart: the inbox verifies with it on the next delivery;</item>
/// <item>only a global administrator can set it, and only for a registered slot;</item>
/// <item>a rotation keeps the previous key verifying until the new one is used;</item>
/// <item>a revocation also stops a key the configuration still mounts;</item>
/// <item>a verify-only delivery (the "Test connection" button) stores nothing.</item>
/// </list>
/// </summary>
public class InstanceSecretsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminPartition = "Admin";
    private const string PlatformAdmin = "platform-boss";
    private const string PlainUser = "plain-jane";

    /// <summary>The inbox's shared key, and a slot for per-sender children under it.</summary>
    private const string InboxKey = "Test:InboxSecret";
    private const string FleetSecret = "the-fleet-wide-secret";

    /// <summary>A single-key slot, as a sender's signing key is registered.</summary>
    private const string SigningKey = "Test:SigningKey";

    /// <summary>A sender whose key the CONFIGURATION mounts (the vault/CSI shape).</summary>
    private const string MountedSender = "mounted";
    private const string MountedSecret = "the-mounted-senders-key";

    private const string Target = "Test/Inbox";

    private static readonly string MasterKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly CapturingLoggerProvider logs = new();

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(AdminPartition) { Name = "Admin", NodeType = "Markdown" },
                new MeshNode(PlainUser) { Name = "Plain Jane", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminPartition),
                AssignmentNodeFactory.UserRole(PlainUser, "Admin", PlainUser))
            .AddWebhookInbox()
            .AddInstanceSecretSlot(SigningKey)
            .AddInstanceSecretSlot(InboxKey + ":*")
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                return services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Ai:KeyProtection:MasterKey"] = MasterKey,
                        [InboxKey] = FleetSecret,
                        [$"{InboxKey}:{MountedSender}"] = MountedSecret,
                    }).Build());
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    private Task<InstanceSecretStatus> SetAs(string user, string key, string value, TimeSpan? keepPrevious = null)
    {
        using (Access.SwitchAccessContext(Identity(user)))
            return InstanceSecrets.Set(Mesh, key, value, keepPrevious).FirstAsync().Timeout(Budget)
                .Await(TestContext.Current.CancellationToken);
    }

    private Task<InstanceSecretStatus> DisableAs(string user, string key)
    {
        using (Access.SwitchAccessContext(Identity(user)))
            return InstanceSecrets.Disable(Mesh, key).FirstAsync().Timeout(Budget)
                .Await(TestContext.Current.CancellationToken);
    }

    private InstanceSecretCatalog Catalog => Mesh.ServiceProvider.GetRequiredService<InstanceSecretCatalog>();

    /// <summary>Waits until the live catalog reflects <paramref name="predicate"/> — the "no restart" property.</summary>
    private Task WaitForCatalog(Func<InstanceSecretCatalog, bool> predicate, string because) =>
        Catalog.Changes.Where(_ => predicate(Catalog)).Should().Within(Budget).Emit(because);

    private Task<MeshNode?> StoredNode(string key) =>
        Access.RunAsSystem(() => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{InstanceSecrets.PathOf(key)}")).Take(1)
                .Select(c => c.Items.FirstOrDefault()))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private static KeyValuePair<string, string> Sign(string body, string secret) =>
        new(WebhookInbox.SignatureHeader, "sha256=" + Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant());

    private Task<WebhookInbox.DeliveryResult> Deliver(string body, params KeyValuePair<string, string>[] headers) =>
        WebhookInbox.Deliver(Mesh, [new WebhookInbox.WebhookTarget(Target, InboxKey)], Target, "application/json", headers, body)
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private Task CreateTarget() =>
        Access.RunAsSystem(() => MeshService.CreateOrUpdateNode(new MeshNode(Target)
            {
                Name = Target,
                NodeType = "Markdown",
                Content = new MarkdownContent { Content = "# Inbox\n" },
            }))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private Task<int> InboxCount() =>
        Access.RunAsSystem(() => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{Target}/{WebhookInbox.InboxContainer} scope:children")).Take(1))
            .Select(c => c.Items.Count(n => n.NodeType == WebhookInbox.NodeType))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    // ───────────────────────────── pure rules ─────────────────────────────

    [Theory]
    [InlineData("Hosting:ControlInbox:Secret", "Hosting:ControlInbox:Secret", true)]
    [InlineData("Hosting:ControlInbox:Secret", "hosting:controlinbox:secret", true)]
    [InlineData("Hosting:PlatformWebhookSecret:*", "Hosting:PlatformWebhookSecret:fabrikam", true)]
    // The section itself — the FLEET secret — is never admitted by a child slot.
    [InlineData("Hosting:PlatformWebhookSecret:*", "Hosting:PlatformWebhookSecret", false)]
    // Exactly one more segment: no grandchildren, no empty child, no odd characters.
    [InlineData("Hosting:PlatformWebhookSecret:*", "Hosting:PlatformWebhookSecret:fabrikam:Previous", false)]
    [InlineData("Hosting:PlatformWebhookSecret:*", "Hosting:PlatformWebhookSecret:", false)]
    [InlineData("Hosting:PlatformWebhookSecret:*", "Hosting:PlatformWebhookSecret:pe arl", false)]
    [InlineData("Hosting:ControlInbox:Secret", "Auth:GlobalAdmins:0", false)]
    public void A_slot_admits_exactly_the_keys_it_names(string pattern, string key, bool admitted) =>
        Assert.Equal(admitted, InstanceSecrets.Admits(pattern, key));

    [Fact]
    public void The_fingerprint_is_deterministic_short_and_names_its_algorithm()
    {
        var key = SecretFingerprint.Generate();
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.NotEqual(key, SecretFingerprint.Generate());
        var fingerprint = SecretFingerprint.Of(key);
        Assert.Equal(fingerprint, SecretFingerprint.Of(key));
        Assert.Matches("^sha256:[0-9a-f]{12}$", fingerprint!);
        Assert.DoesNotContain(key[..12], fingerprint!);
        Assert.Null(SecretFingerprint.Of(""));
    }

    [Theory]
    // 16 hex digits: alphabet 36 → floor(16 × 5.17) = 82 bits → withheld.
    [InlineData("0123456789abcdef", false)]
    // A human password: withheld, so the fingerprint is no guessing oracle.
    [InlineData("Summer2026!", false)]
    // 32 hex digits: floor(32 × 5.17) = 165 bits → shown.
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    // Digits only: alphabet 10 → needs 39 digits for 128 bits; 38 is withheld.
    [InlineData("12345678901234567890123456789012345678", false)]
    [InlineData("123456789012345678901234567890123456789", true)]
    public void A_low_entropy_value_gets_no_fingerprint(string value, bool shown)
    {
        var fingerprint = SecretFingerprint.Of(value);
        if (shown)
            Assert.StartsWith(SecretFingerprint.Prefix, fingerprint);
        else
            Assert.Equal(SecretFingerprint.Withheld, fingerprint);
    }

    // ───────────────────────────── the store ─────────────────────────────

    [Fact]
    public async Task A_saved_key_is_stored_encrypted_resolved_live_and_never_logged()
    {
        var key = SecretFingerprint.Generate();

        var status = await SetAs(PlatformAdmin, SigningKey, key);

        Assert.True(status.Secret.Present);
        Assert.True(status.Secret.Enabled == true);
        Assert.Equal(InstanceSecretStatus.FromPortal, status.Origin);
        Assert.Equal(SecretSources.Paste, status.Secret.Source);
        Assert.Equal(SecretFingerprint.Of(key), status.Secret.Fingerprint);
        Assert.Equal(PlatformAdmin, status.Secret.SetBy);

        // Encrypted at rest: the node carries only enc: ciphertext and never the value.
        var node = await StoredNode(SigningKey);
        Assert.NotNull(node);
        var content = node!.ContentAs<InstanceSecretContent>(Mesh.JsonSerializerOptions);
        Assert.NotNull(content);
        Assert.StartsWith("enc:", content!.EncryptedValue);
        Assert.DoesNotContain(key, System.Text.Json.JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions));

        // Live: the running process signs with it — no restart, no configuration change.
        await WaitForCatalog(_ => InstanceSecrets.Resolve(Mesh, SigningKey) == key,
            "a key saved in the portal is what the signing code resolves, without a restart");
        Assert.Equal(key, InstanceSecrets.Resolve(Mesh, SigningKey));

        // Never logged — neither the value nor any fragment long enough to matter.
        Assert.DoesNotContain(logs.Messages, m => m.Contains(key[..16], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_a_global_administrator_can_set_a_secret_and_nothing_is_written_otherwise()
    {
        var refusal = await Assert.ThrowsAsync<InstanceSecretException>(
            () => SetAs(PlainUser, SigningKey, SecretFingerprint.Generate()));
        Assert.Contains("global administrator", refusal.Message);
        Assert.Null(await StoredNode(SigningKey));
    }

    [Fact]
    public async Task A_key_no_slot_admits_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<InstanceSecretException>(
            () => SetAs(PlatformAdmin, InboxKey, "would-replace-the-fleet-secret"));
        Assert.Contains("not a setting", refusal.Message);
        Assert.Null(await StoredNode(InboxKey));
    }

    // ───────────────────────────── the inbox ─────────────────────────────

    [Fact]
    public async Task The_inbox_verifies_a_portal_issued_sender_key_live_and_records_the_use()
    {
        await CreateTarget();
        var key = SecretFingerprint.Generate();
        await SetAs(PlatformAdmin, $"{InboxKey}:fabrikam", key);
        await WaitForCatalog(c => c.Entry($"{InboxKey}:fabrikam")?.Current == key, "the issued key is live");

        const string body = """{"event":"self-update-available","deployment":"fabrikam"}""";
        var result = await Deliver(body, Sign(body, key));

        Assert.Equal(WebhookInbox.DeliveryStatus.Accepted, result.Status);
        Assert.True(result.SignatureVerified);
        Assert.Equal("fabrikam", result.SenderKey);

        await WaitForCatalog(c => c.Entry($"{InboxKey}:fabrikam")?.Content.LastUseOk == true,
            "a verified delivery is recorded as the key's last verified use");

        // A key nobody issued is still refused, and stores nothing.
        var before = await InboxCount();
        var forged = await Deliver(body, Sign(body, SecretFingerprint.Generate()));
        Assert.Equal(WebhookInbox.DeliveryStatus.SignatureInvalid, forged.Status);
        Assert.Equal(before, await InboxCount());
    }

    [Fact]
    public async Task A_verify_only_test_verifies_names_the_sender_and_stores_nothing()
    {
        await CreateTarget();
        var key = SecretFingerprint.Generate();
        await SetAs(PlatformAdmin, $"{InboxKey}:fabrikam", key);
        await WaitForCatalog(c => c.Entry($"{InboxKey}:fabrikam")?.Current == key, "the issued key is live");
        var before = await InboxCount();

        const string body = """{"event":"self-update-key-test","deployment":"fabrikam"}""";
        var verifyOnly = new KeyValuePair<string, string>(WebhookInbox.VerifyOnlyHeader, "true");

        var accepted = await Deliver(body, Sign(body, key), verifyOnly);
        Assert.Equal(WebhookInbox.DeliveryStatus.Accepted, accepted.Status);
        Assert.True(accepted.VerifyOnly);
        Assert.True(accepted.SignatureVerified);
        Assert.Equal("fabrikam", accepted.SenderKey);
        Assert.Null(accepted.NodePath);

        var mismatch = await Deliver(body, Sign(body, "a-key-typed-wrong"), verifyOnly);
        Assert.Equal(WebhookInbox.DeliveryStatus.SignatureInvalid, mismatch.Status);

        Assert.Equal(before, await InboxCount());
    }

    [Fact]
    public async Task A_rotation_accepts_both_keys_until_the_new_one_is_used()
    {
        await CreateTarget();
        var oldKey = SecretFingerprint.Generate();
        var newKey = SecretFingerprint.Generate();
        await SetAs(PlatformAdmin, $"{InboxKey}:fabrikam", oldKey);
        await WaitForCatalog(c => c.Entry($"{InboxKey}:fabrikam")?.Current == oldKey, "the first key is live");

        var rotated = await SetAs(PlatformAdmin, $"{InboxKey}:fabrikam", newKey, keepPrevious: TimeSpan.FromDays(1));
        Assert.Equal(SecretFingerprint.Of(oldKey), rotated.PreviousFingerprint);
        await WaitForCatalog(c => c.Entry($"{InboxKey}:fabrikam") is { Current: var cur, Previous: var prev }
                                  && cur == newKey && prev == oldKey,
            "during a rotation the catalog holds both keys");

        // The deployment has not re-entered its key yet: its OLD key still announces.
        const string body = """{"event":"self-update-available","deployment":"fabrikam"}""";
        var withOld = await Deliver(body, Sign(body, oldKey));
        Assert.Equal("fabrikam", withOld.SenderKey);

        // Its first delivery with the NEW key completes the rotation: the old key stops verifying.
        var withNew = await Deliver(body, Sign(body, newKey));
        Assert.Equal("fabrikam", withNew.SenderKey);
        await WaitForCatalog(c => c.Entry($"{InboxKey}:fabrikam")?.Previous is null,
            "the first use of the new key ends the rotation");
        var oldAgain = await Deliver(body, Sign(body, oldKey));
        Assert.Equal(WebhookInbox.DeliveryStatus.SignatureInvalid, oldAgain.Status);
    }

    [Fact]
    public async Task A_revocation_also_stops_a_key_the_configuration_mounts()
    {
        await CreateTarget();
        const string body = """{"event":"self-update-available","deployment":"mounted"}""";
        Assert.Equal(MountedSender, (await Deliver(body, Sign(body, MountedSecret))).SenderKey);

        await DisableAs(PlatformAdmin, $"{InboxKey}:{MountedSender}");
        await WaitForCatalog(c => c.Entry($"{InboxKey}:{MountedSender}")?.IsDisabled == true, "the revocation is live");

        var refused = await Deliver(body, Sign(body, MountedSecret));
        Assert.Equal(WebhookInbox.DeliveryStatus.SignatureInvalid, refused.Status);
        Assert.Equal(false, InstanceSecrets.StatusOf(Mesh, $"{InboxKey}:{MountedSender}").Secret.Enabled);

        // The fleet secret itself is untouched.
        const string fleetBody = """{"event":"build"}""";
        var fleet = await Deliver(fleetBody, Sign(fleetBody, FleetSecret));
        Assert.Equal(WebhookInbox.DeliveryStatus.Accepted, fleet.Status);
        Assert.Null(fleet.SenderKey);
    }

    [Fact]
    public async Task Generate_returns_the_value_once_disable_enable_delete_and_recover_change_only_the_state()
    {
        var key = $"{InboxKey}:{MountedSender}";
        InstanceSecrets.Generated generated;
        using (Access.SwitchAccessContext(Identity(PlatformAdmin)))
            generated = await InstanceSecrets.Generate(Mesh, key).FirstAsync().Timeout(Budget)
                .Await(TestContext.Current.CancellationToken);
        Assert.Matches("^[0-9a-f]{64}$", generated.Value);
        Assert.Equal(SecretSources.Generate, generated.Status.Secret.Source);
        Assert.Equal(SecretFingerprint.Of(generated.Value), generated.Status.Secret.Fingerprint);
        await WaitForCatalog(_ => InstanceSecrets.Resolve(Mesh, key) == generated.Value, "the generated value is live");

        await DisableAs(PlatformAdmin, key);
        await WaitForCatalog(_ => InstanceSecrets.Resolve(Mesh, key) is null, "a disabled key is not used, nor is the mounted one");

        using (Access.SwitchAccessContext(Identity(PlatformAdmin)))
            await InstanceSecrets.Enable(Mesh, key).FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        await WaitForCatalog(_ => InstanceSecrets.Resolve(Mesh, key) == generated.Value, "enabling restores the same value");

        using (Access.SwitchAccessContext(Identity(PlatformAdmin)))
            await InstanceSecrets.Delete(Mesh, key).FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        await WaitForCatalog(_ => InstanceSecrets.Resolve(Mesh, key) == MountedSecret,
            "a deleted portal key gives way to the mounted one");
        var deleted = InstanceSecrets.StatusOf(key, Catalog.Entry(key), null);
        Assert.True(deleted.Secret.Deleted);
        Assert.NotNull(deleted.Secret.RecoverableUntil);

        using (Access.SwitchAccessContext(Identity(PlatformAdmin)))
            await InstanceSecrets.Recover(Mesh, key).FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        await WaitForCatalog(_ => InstanceSecrets.Resolve(Mesh, key) == generated.Value, "recovering restores the portal key");

        Assert.DoesNotContain(logs.Messages, m => m.Contains(generated.Value[..16], StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_direct_write_is_refused_and_a_plain_value_is_never_used()
    {
        var key = $"{InboxKey}:intruder";
        // A person with rights on Admin writes a secret node DIRECTLY, around the verbs, with a plain value.
        var direct = new MeshNode(InstanceSecrets.IdOf(key), InstanceSecrets.Partition)
        {
            NodeType = InstanceSecrets.NodeType,
            Content = new InstanceSecretContent { ConfigKey = key, EncryptedValue = "plain-injected-key" },
        };
        Exception? refused = null;
        using (Access.SwitchAccessContext(Identity(PlatformAdmin)))
            try
            {
                await MeshService.CreateNode(direct).FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                refused = ex;
            }
        Assert.NotNull(refused);
        Assert.Null(await StoredNode(key));

        // Even content that got past the guard (written as system) is never used unless it is enc:-tagged.
        var entry = InstanceSecretCatalog.CatalogEntry.From(InstanceSecrets.PathOf(key),
            new InstanceSecretContent { ConfigKey = key, EncryptedValue = "plain-injected-key" }, s => s, DateTimeOffset.UtcNow);
        Assert.Null(entry.Current);
    }

    [Fact]
    public async Task A_value_carrying_the_encryption_tag_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<InstanceSecretException>(
            () => SetAs(PlatformAdmin, SigningKey, "enc:v1:not-really-encrypted"));
        Assert.Equal("secret.error.reservedPrefix", refusal.Text.Key);
        Assert.Null(await StoredNode(SigningKey));
    }

    [Theory]
    [InlineData("Test:InboxSecret:a--b")]
    [InlineData("Test:InboxSecret:--")]
    public void A_key_that_would_collide_in_the_node_id_is_not_admitted(string key) =>
        Assert.False(InstanceSecrets.Admits(InboxKey + ":*", key));

    [Fact]
    public void Two_spellings_of_one_key_address_one_node() =>
        Assert.Equal(InstanceSecrets.PathOf("Test:InboxSecret:Fabrikam"), InstanceSecrets.PathOf("test:inboxsecret:fabrikam"));

    [Fact]
    public async Task A_rotation_overlap_is_clamped_to_the_maximum()
    {
        var key = $"{InboxKey}:fabrikam";
        await SetAs(PlatformAdmin, key, SecretFingerprint.Generate());
        await WaitForCatalog(c => c.Entry(key) is not null, "the first key is live");
        var rotated = await SetAs(PlatformAdmin, key, SecretFingerprint.Generate(), keepPrevious: TimeSpan.FromDays(3650));
        Assert.NotNull(rotated.PreviousUntil);
        Assert.True(rotated.PreviousUntil <= DateTimeOffset.UtcNow + InstanceSecrets.DefaultRotationOverlap + TimeSpan.FromMinutes(1),
            $"a ten-year overlap is clamped to {InstanceSecrets.DefaultRotationOverlap}: {rotated.PreviousUntil}");
    }

    /// <summary>Collects every formatted log message, so a test can prove a value never reached one.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();

        public IEnumerable<string> Messages => messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new Capture(messages);

        public void Dispose() { }

        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                sink.Enqueue(formatter(state, exception));
                if (exception is not null)
                    sink.Enqueue(exception.ToString());
            }
        }
    }
}
