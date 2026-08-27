using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// E2E (#1809): the stale-build banner is VISIBLE IN A BROWSER — not merely written into the
/// <c>$Banner</c> sidecar slot.
///
/// <para>🚨 <b>Why the mesh-level test is not this test.</b> <c>StaleBuildBannerTest</c> asserts the
/// rendered CONTROL TREE through a real mesh, which proves the server writes a banner control into
/// the slot. It does not prove a user sees anything: the Blazor binding
/// (<c>LayoutAreaView</c> reducing <c>$Banner</c> and drawing it above the content) sits between the
/// two, and "renders empty" is precisely what lives there — one evening on this codebase produced
/// four separate false-greens, one of which was every route answering HTTP 200 in 0.22 s while
/// rendering nothing at all. A server-side control assertion and a visible banner are different
/// claims; only the second one was asked for.</para>
///
/// <para><b>The seam that makes this deterministic — no recompile, no sleep.</b> The banner state is
/// a pure function of two strings: the assembly path the instance BOUND at activation versus the one
/// its NodeType currently PUBLISHES. So the test never compiles twice and never waits on a clock: it
/// writes a different <c>latestAssemblyPath</c> onto the type node through the ordinary mutation API
/// (<c>/api/mesh/patch</c> is an RFC 7396 merge routed through
/// <c>GetMeshNodeStream(typePath).Update</c>, so every other field — the collection, the framework
/// version, the Ok status — is preserved and the build stays USABLE), and then waits on the DOM
/// condition through Playwright's auto-waiting locator. Nothing ever loads that path (the test never
/// recycles), so a synthetic value is safe; only the COMPARISON is under test.</para>
///
/// <para>🚨 <b>Negative control first, and it must be on the TEXT.</b> Asserting only that the banner
/// appears would pass equally for a banner that is always on — which would put a "newer build
/// available" notice above every page in the portal. The container is the wrong thing to assert on:
/// the slot is written with an EMPTY control while there is no offer, so
/// <c>.meshweaver-stale-build-banner</c> is present-but-empty on a perfectly current page. What
/// distinguishes the two states is the offer text, which is what both halves read.</para>
///
/// <para>🚨 <b>What a text match here does and does not prove — read before tightening this.</b>
/// The assertions are on the offer TEXT inside the banner's own container, which is what makes them
/// hold in a portal whose Blazor VIEW PACKS are missing. Since the view definitions left this repo
/// (<c>db552ffbf</c>, 2026-08-25) a dev <c>dotnet run</c> of <c>Memex.Portal.Monolith</c> has no
/// <c>MarkdownControl</c> view registered, so every control renders as its model's <c>ToString()</c>
/// — the words are on screen, correctly placed, but unstyled. That state is what the first run of
/// this test met, and the run still DISCRIMINATED: before the publish the slot held the empty
/// <c>StackControl</c> the no-offer path writes, after it the offer. So the claim established is
/// "the banner reaches the user's screen, in its slot, above the content" — genuinely more than the
/// mesh-level test proves, and genuinely less than "it renders as styled markdown". Asserting the
/// rendered anchor instead would be the stronger claim; it cannot be made until a locally-launched
/// portal carries its view packs again.</para>
///
/// <para>Gated like the rest of the suite: set <c>E2E_BASE_URL</c> (or <c>E2E_LAUNCH=1</c>) to run;
/// otherwise it Skips.</para>
/// </summary>
[Collection("portal-e2e")]
public class StaleBuildBannerE2ETest(PortalFixture fixture)
{
    /// <summary>The rendered offer text — <c>ui.mdStaleBuildAvailable</c>, minus its markdown
    /// emphasis so the assertion reads the same whether or not the renderer bolds it.</summary>
    private const string OfferText = "newer build of this type is available";

    /// <summary>A marker only THIS type's view can produce, so "the content rendered" is unambiguous.</summary>
    private const string ContentMarker = "STALE_BANNER_E2E_CONTENT";

    /// <summary>The adornment's own container (LayoutAreaView.razor). Present-but-empty when there
    /// is no offer, which is exactly why the assertions below read its TEXT and not its existence.</summary>
    private const string BannerContainer = ".meshweaver-stale-build-banner";

    [Fact(Timeout = 300_000)]
    public async Task ANewerPublishedBuild_ShowsTheBannerAboveTheStillWorkingPage()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        var partition = fixture.UserId;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeTypeId = $"BannerE2EType{suffix}";
        var typePath = $"{partition}/{nodeTypeId}";
        var instanceId = $"banner-e2e-instance-{suffix}";

        // 1. A NodeType that genuinely COMPILES, so its instance binds a real assembly path — the
        //    "bound" half of the comparison under test. Its view carries a unique marker so step 5
        //    can tell "the page still works" from "the page is only a banner".
        await fixture.CreateNodeAsync(context, token, $$"""
            {
              "id": "{{nodeTypeId}}",
              "namespace": "{{partition}}",
              "name": "Banner E2E Type",
              "nodeType": "NodeType",
              "content": {
                "$type": "NodeTypeDefinition",
                "configuration": "config => config.AddLayout(layout => layout.WithView(\"Overview\", (host, ctx) => Controls.Markdown(\"{{ContentMarker}}\")))"
              }
            }
            """);

        // 2. An instance of it — activating it is what BINDS the assembly.
        await fixture.CreateNodeAsync(context, token, $$"""
            {
              "id": "{{instanceId}}",
              "namespace": "{{partition}}",
              "name": "Banner E2E Instance",
              "nodeType": "{{typePath}}",
              "state": "Active"
            }
            """);

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync($"{fixture.BaseUrl}/{partition}/{instanceId}/Overview",
            new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });

        // 3. The instance's OWN content renders first. A cold Roslyn compile is slow, so the budget
        //    is generous — but it MUST resolve, or every assertion below would be measuring a page
        //    that never came up.
        await Shot(page, "banner-e2e-1-content", async () =>
            await page.GetByText(ContentMarker).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 180_000
            }));

        // 4. NEGATIVE CONTROL — with the page fully rendered on the type's CURRENT build, there is
        //    no offer, so no offer text is on screen anywhere. Read the whole body rather than the
        //    banner container: an always-on banner drawn somewhere else would still be a bug.
        (await page.Locator("body").InnerTextAsync())
            .Should().NotContain(OfferText,
                "an instance running its type's CURRENT build must show no banner — otherwise "
                + "every page in the portal carries a 'newer build available' notice");

        // 5. THE SIGNAL: the type publishes a DIFFERENT assembly. A merge patch touching only
        //    latestAssemblyPath keeps the build usable (the watcher's HasUsableBuild gate reads the
        //    collection + framework version, which the merge leaves alone).
        await fixture.PatchNodeAsync(context, token, typePath,
            $$$"""{"content":{"latestAssemblyPath":"superseded-by-e2e-{{{suffix}}}.dll"}}""");

        // 6. POSITIVE — the banner becomes VISIBLE, asserted on the rendered element's text. The
        //    watcher throttles on a 10 s settle window before publishing the offer, so this waits on
        //    the CONDITION through Playwright's auto-waiting locator, never on a sleep.
        var banner = page.Locator(BannerContainer).GetByText(OfferText);
        await Shot(page, "banner-e2e-2-banner", async () =>
            await banner.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 120_000
            }));

        // The offer is actionable: the banner links to the node's own Recycle area, which is the
        // button the user presses (RecycleLayoutArea owns the confirmation).
        (await page.Locator(BannerContainer).InnerTextAsync())
            .Should().Contain("Recycle", "the offer has to be clickable, not just informative");

        // 7. …and it is an ADORNMENT, not a replacement: the instance is still serving its own
        //    content underneath. A page that lost its content — or recycled itself — is the
        //    restart-storm regression this feature replaced.
        await page.GetByText(ContentMarker).First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });
    }

    /// <summary>
    /// Runs a wait and leaves a full-page SCREENSHOT behind either way.
    ///
    /// <para>🚨 The screenshot on FAILURE is the point: a Playwright timeout says only "the locator
    /// never matched", and inferring why from that text is how this suite has previously chased the
    /// wrong cause. The image says whether the page was blank, still spinning, showing a compile
    /// overlay, or rendering fine with no banner — four very different defects behind one message.
    /// The exception is re-thrown untouched; nothing is swallowed.</para>
    /// </summary>
    private static async Task Shot(IPage page, string name, Func<Task> wait)
    {
        try
        {
            await wait();
        }
        finally
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(Path.GetTempPath(), $"{name}.png"),
                FullPage = true
            });
        }
    }
}
