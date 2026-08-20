using System;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The transport half of the display-time seam (#455 / #513, regression #1936): a SUBSCRIBER's
/// <see cref="AccessContext.TimeZoneId"/> must reach the layout-area render that runs on the
/// OWNING node's hub — a different hub, a different action block, and (in production) a different
/// process from the one the viewer's identity was resolved on.
///
/// <para>Both entry shapes are exercised, because they carry the identity differently and the
/// production report claimed one worked while the other did not:</para>
/// <list type="bullet">
///   <item><b>Request</b> — the identity is on <see cref="AccessService.Context"/> when the
///     subscribe is issued (an HTTP request, an MCP verb, an SSR pass).</item>
///   <item><b>Circuit</b> — the identity is on <see cref="AccessService.CircuitContext"/> and
///     <see cref="AccessService.Context"/> is null, which is exactly what
///     <c>CircuitAccessHandler</c> establishes per Blazor inbound activity.</item>
/// </list>
///
/// <para>🚨 The area resolves the zone the way every render site must:
/// <c>AccessService.ViewerZoneId()</c> ONCE on the render turn, then
/// <see cref="DisplayTimeExtensions.ToDisplayTime(DateTimeOffset,string?)"/> with that captured
/// value — never <c>.ToLocalTime()</c> / <c>.LocalDateTime</c>, which resolve to the SERVER process
/// zone (UTC in the deployment container, so the conversion is a silent no-op).</para>
///
/// <para>The instants cover both DST directions and the DATE ROLLOVER, because a hard-coded offset
/// passes one row and fails the other, and a missed conversion across midnight reads as an
/// off-by-one day rather than as a time-zone bug.</para>
/// </summary>
public class ViewerTimeZoneRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "clock/Viewer";

    /// <summary>
    /// One area per stored instant — both DST directions plus the midnight rollover. Separate
    /// AREAS rather than one area with an id, so the assertion never depends on how an area id is
    /// folded into the rendered area name.
    /// </summary>
    private static readonly (string Area, DateTimeOffset Utc)[] Clocks =
    [
        ("ClockSummer", new DateTimeOffset(2026, 7, 20, 14, 32, 0, TimeSpan.Zero)),   // CEST → 16:32
        ("ClockWinter", new DateTimeOffset(2026, 1, 20, 14, 32, 0, TimeSpan.Zero)),   // CET  → 15:32
        ("ClockRollover", new DateTimeOffset(2026, 7, 29, 23, 30, 0, TimeSpan.Zero)), // CEST → 07-30 01:30
    ];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(MeshNode.FromPath(NodePath) with
            {
                Name = "Viewer clock",
                State = MeshNodeState.Active,
                HubConfiguration = config => config.AddLayout(layout =>
                {
                    foreach (var (area, utc) in Clocks)
                    {
                        var instant = utc;
                        layout = layout.WithView(area, (LayoutAreaHost host, RenderingContext _) =>
                            (UiControl)Controls.Markdown(Stamp(host, instant)));
                    }
                    return layout;
                })
            });

    /// <summary>
    /// The production render shape, verbatim: resolve the viewer's zone ONCE on this turn, then
    /// format the stored UTC instant through it.
    /// </summary>
    private static string Stamp(LayoutAreaHost host, DateTimeOffset storedUtc)
    {
        var zone = host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId();
        var local = DisplayTimeExtensions.ToDisplayTime(storedUtc, zone);
        return $"zone={zone ?? "(null)"} rendered={local:yyyy-MM-dd HH:mm} (UTC{local:zzz})";
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [Theory(Timeout = 120_000)]
    [InlineData("ClockSummer", "2026-07-20 16:32")]
    [InlineData("ClockWinter", "2026-01-20 15:32")]
    // The date MOVES — a missed conversion here reads as an off-by-one day, not as a zone bug.
    [InlineData("ClockRollover", "2026-07-30 01:30")]
    public async Task RequestViewerZone_ReachesTheOwnerSideRender(string area, string expected)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var viewer = TestUsers.Admin with { TimeZoneId = "Europe/Zurich" };

        string markdown;
        using (access.SwitchAccessContext(viewer))
            markdown = await Render(area);

        Output.WriteLine($"request/{area}: {markdown}");
        markdown.Should().Contain("Europe/Zurich");
        markdown.Should().Contain(expected);
    }

    /// <summary>
    /// The Blazor-circuit shape: the viewer's identity lives ONLY on
    /// <see cref="AccessService.CircuitContext"/> — <c>CircuitAccessHandler</c> sets that per
    /// inbound activity and never <see cref="AccessService.Context"/>. This is the path #1936 was
    /// reported against.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("ClockSummer", "2026-07-20 16:32")]
    [InlineData("ClockWinter", "2026-01-20 15:32")]
    [InlineData("ClockRollover", "2026-07-30 01:30")]
    public async Task CircuitViewerZone_ReachesTheOwnerSideRender(string area, string expected)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var viewer = TestUsers.Admin with { TimeZoneId = "Europe/Zurich" };

        access.SetCircuitContext(viewer);
        string markdown;
        try
        {
            access.Context?.TimeZoneId.Should().BeNull(
                "the circuit shape is: identity on CircuitContext, nothing on Context — a test that "
                + "leaked the zone onto Context would prove nothing about the circuit");
            markdown = await Render(area);
        }
        finally
        {
            access.SetCircuitContext(null);
        }

        Output.WriteLine($"circuit/{area}: {markdown}");
        markdown.Should().Contain("Europe/Zurich");
        markdown.Should().Contain(expected);
    }

    /// <summary>
    /// An unset zone degrades to UTC — never to the SERVER's zone, which looks right in CI and is
    /// wrong in Zurich.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task NoViewerZone_RendersUtc_NotTheServerZone()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        string markdown;
        using (access.SwitchAccessContext(TestUsers.Admin with { TimeZoneId = null }))
            markdown = await Render("ClockSummer");

        Output.WriteLine($"nozone: {markdown}");
        markdown.Should().Contain("zone=(null)");
        markdown.Should().Contain("2026-07-20 14:32");
        markdown.Should().Contain("(UTC+00:00)");
    }

    private async Task<string> Render(string area)
    {
        var client = GetClient();
        var reference = new LayoutAreaReference(area);
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(NodePath), reference);
        try
        {
            var control = await stream.GetControlStream(reference.Area!)
                .Should().Within(60.Seconds()).Match(c => c is not null,
                    $"the '{area}' render must produce a frame");
            return (control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty;
        }
        finally
        {
            stream.Dispose();
        }
    }
}
