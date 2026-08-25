using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 THE PORTAL'S USER DIRECTORY IS AN ENUMERATION — issue #1936.
///
/// <para><b>The defect.</b> <see cref="UserIdentityCache"/> is the ONE index that turns an
/// authenticated caller's email into their mesh <c>User</c> node, and that node is the only source
/// of <see cref="AccessContext.TimeZoneId"/> and <see cref="AccessContext.Locale"/>
/// (<see cref="MeshUserProjection"/>). It used to issue a bare <c>nodeType:User</c> query with no
/// limit. That query carries no <c>path:</c> and no <c>namespace:</c>, so on Postgres it is the
/// UNPINNED shape served by <c>PostgreSqlCrossSchemaQueryProvider</c>, which at the time answered a
/// request stating no limit with a PAGE: the 50 most recently modified matches, ordered
/// <c>last_modified DESC</c>. Every other user was silently absent from the directory.</para>
///
/// <para><b>Why it is invisible, and why it gets worse on its own.</b> A missing row makes
/// <see cref="UserIdentityCache.Lookup"/> answer a DETERMINATE <c>Unknown</c> — the index IS
/// hydrated, so "not in here" reads as "no such user". Both context builders then keep the claims
/// SEED: <c>ObjectId</c>, <c>Email</c> and <c>IsVirtual</c> are all still correct, so nothing looks
/// wrong, no warning is logged (the degradation warning fires only on <c>Unavailable</c>), and
/// <c>CircuitAccessHandler</c> does not even start its identity repair. What the seed does NOT
/// carry is the time zone and the language — so every timestamp renders UTC and every string
/// English. And a <c>User</c> node's <c>last_modified</c> only moves when the PROFILE is written,
/// so the longer a user goes without editing their profile the more certainly they fall off the
/// page: self-reinforcing and invisible, the same shape as #1216 and #1326.</para>
///
/// <para><b>Why this test is on Postgres.</b> The in-memory <c>StorageAdapterMeshQueryProvider</c>
/// and the per-schema <c>PostgreSqlMeshQuery</c> both treat "no limit" as UNBOUNDED, so the
/// identical code is correct on every adapter a unit test uses. Only the cross-schema fan-out caps
/// it — which is exactly why the defect shipped.</para>
/// </summary>
[Collection("PostgreSql")]
public class UserDirectoryCompletenessTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    /// <summary>
    /// Users seeded ahead of the one under test. Comfortably above the cross-schema fan-out's
    /// 50-row default — at or below it this test reproduces nothing.
    /// </summary>
    private const int CrowdSize = 60;

    /// <summary>The viewer whose profile must still resolve. Seeded FIRST, then buried under
    /// <see cref="CrowdSize"/> more recently modified users, so it is off the default page.</summary>
    private const string ForgottenUser = "forgotten";

    private const string ForgottenEmail = "forgotten@meshweaver.io";
    private const string ForgottenZone = "Europe/Zurich";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private IObservable<Unit> ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastOrDefaultAsync();

    /// <summary>
    /// The directory read's two properties, asserted without a mesh. Cheap, and it names WHAT the
    /// behavioural test below is protecting — a future edit that drops either one fails here first.
    /// </summary>
    [Fact]
    public void DirectoryQuery_IsAnEnumerationReadAsSystem()
    {
        UserIdentityCache.DirectoryQuery.Limit.Should().Be(MeshQueryRequest.NoLimit,
            "the user directory is an ENUMERATION — a user missing from it does not shorten a list, "
            + "it changes who the portal thinks the caller is");
        UserIdentityCache.DirectoryQuery.UserId.Should().Be(WellKnownUsers.System,
            "a process-wide directory resolved once must not inherit the permissions of whichever "
            + "caller happened to construct the singleton — off a circuit's own call tree that is "
            + "nobody at all, and the index then hydrates EMPTY and answers 'no such user' forever");
    }

    /// <summary>
    /// The regression itself, end to end: a user buried under more recently modified ones is still
    /// found by the directory, and their stored zone still reaches the <see cref="AccessContext"/>.
    /// Before the fix the lookup answered a determinate <c>Unknown</c> and the projection never ran.
    ///
    /// <para>🚨 <b>What this test can and cannot fail on, stated so nobody mistakes it for a
    /// falsification of <c>Complete()</c>.</b> The 50-row page that caused #1936 lived on a paging
    /// fan-out overload no runtime caller could reach; it is deleted (#2048), so removing
    /// <c>.Complete()</c> from <c>DirectoryQuery</c> leaves this test PASSING — only the property
    /// assertion above fails. That is the correct division of labour and not a gap to paper over:
    /// this test pins the end-to-end BEHAVIOUR (a buried user resolves, zone and all), the property
    /// test pins the DECLARATION, and neither pretends to reproduce a truncation the runtime no
    /// longer performs.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AUserOutsideTheMostRecentlyModifiedPage_StillResolvesTheirProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Post-v10 every user OWNS a partition, so this is also the production shape of the read:
        // an unpinned query UNIONing one schema per user.
        using (access.ImpersonateAsSystem())
        {
            await SeedUser(ForgottenUser, ForgottenEmail, ForgottenZone, "de");

            // Bury it. Each write stamps a NEWER last_modified, so after this the forgotten user is
            // the OLDEST User node in the mesh and sits well outside the 50-row default page.
            for (var i = 0; i < CrowdSize; i++)
                await SeedUser($"crowd{i:D2}", $"crowd{i:D2}@meshweaver.io", "UTC", "en");
        }

        using var cache = new UserIdentityCache(
            MeshService, Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<UserIdentityCache>>());

        var lookup = await cache.WhenDetermined(ForgottenEmail)
            .Timeout(60.Seconds())
            .FirstAsync()
            .ToTask(ct);

        Output.WriteLine(
            $"lookup: node={lookup.Node?.Path ?? "(null)"} unavailable={lookup.UnavailableReason ?? "(no)"}");

        lookup.Node.Should().NotBeNull(
            $"the directory must contain every user, not the {CrowdSize} most recently modified — a "
            + "determinate 'no such user' here is what makes both context builders keep the claims "
            + "seed, which carries no time zone and no language");

        // The whole point of finding the node: the profile reaches the identity.
        var projected = MeshUserProjection.Apply(
            new AccessContext { ObjectId = ForgottenUser, Email = ForgottenEmail },
            lookup.Node!,
            Mesh.JsonSerializerOptions);

        Output.WriteLine($"projected: tz={projected.TimeZoneId ?? "(null)"} locale={projected.Locale ?? "(null)"}");
        projected.TimeZoneId.Should().Be(ForgottenZone,
            "AccessContext.TimeZoneId is what every render path converts stored UTC through — "
            + "without it the viewer sees the container's clock, which is UTC for everyone");
        projected.Locale.Should().Be("de");

        // And the zone actually converts: 14:32Z is 16:32 in Zurich in July (CEST).
        DisplayTimeExtensions
            .ToDisplayTime(new DateTimeOffset(2026, 7, 20, 14, 32, 0, TimeSpan.Zero), projected.TimeZoneId)
            .ToString("HH:mm")
            .Should().Be("16:32");
    }

    private async Task SeedUser(string id, string email, string zone, string locale)
    {
        await ProvisionPartition(id).Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(MeshNode.FromPath(id) with
        {
            Name = id,
            NodeType = "User",
            State = MeshNodeState.Active,
            Content = new User { Email = email, TimeZoneId = zone, Locale = locale }
        }).Should().Within(60.Seconds()).Emit();
    }
}
