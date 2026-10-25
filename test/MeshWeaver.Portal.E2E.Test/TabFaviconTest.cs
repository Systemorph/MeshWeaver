using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Browser proof that a node page puts its OWN icon in the browser tab — the one thing no unit test
/// can show, because the tab icon is not in the HTTP response at all.
///
/// <para><b>Why a real browser is the only witness.</b> The portal's interactive components render
/// with prerendering off, so the initial HTML carries the site-wide <c>favicon.ico</c> and an EMPTY
/// title; both the title and the icon are filled in by the circuit, through <c>HeadOutlet</c>. A
/// curl of a node page therefore shows no node icon even when the feature works perfectly — and
/// equally would show none if it were broken. Only a connected circuit distinguishes the two.</para>
///
/// <para>Two behaviours are pinned: the icon a page publishes is the node's own (resolved exactly as
/// the app resolves it everywhere else, so a node with no icon of its own reads as its TYPE), and it
/// SWAPS on an in-circuit navigation rather than only on a fresh page load — the whole point being a
/// tab strip you can read, which a favicon that only updates on F5 would not deliver.</para>
///
/// Drives the real portal — Skips unless E2E is enabled (see <see cref="PortalFixture"/>).
/// </summary>
[Collection("portal-e2e")]
public class TabFaviconTest(PortalFixture fixture)
{
    /// <summary>Reads the LAST declared <c>rel="icon"</c> — the one a browser resolves the tab from,
    /// and the position the feature depends on (App.razor declares the site favicon before
    /// <c>HeadOutlet</c> precisely so the page's own icon outranks it).</summary>
    private const string LastIconHrefJs = """
        () => {
          const links = [...document.querySelectorAll('link[rel~="icon"]')];
          const last = links[links.length - 1];
          return last ? last.getAttribute('href') : null;
        }
        """;

    [Fact]
    public async Task ANodePage_PublishesItsOwnIcon_AndSwapsItOnInCircuitNavigation()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // A page carrying NO icon of its own: it must read as its NodeType (a document), never as the
        // portal favicon — that sameness is the defect this fixes.
        var plainPath = await SeedAsync(context, token, "Tab icon — plain (e2e)", icon: null);
        // …and one whose icon is a GLYPH: a value no href can carry, so it only reaches the tab if it
        // is drawn into an svg. Authored icons are the case a user notices first.
        var glyphPath = await SeedAsync(context, token, "Tab icon — glyph (e2e)", icon: "🎯");

        (await fixture.WaitUntilReadableAsync(context, token, plainPath, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the seeded page must be readable before the UI is driven");
        (await fixture.WaitUntilReadableAsync(context, token, glyphPath, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the seeded page must be readable before the UI is driven");

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/{plainPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        page.Url.Should().NotContain("/login");

        // The circuit fills the head in; wait for the icon to stop being the site-wide favicon.
        await WaitForIconAsync(page, "/static/NodeTypeIcons/document.svg");
        (await IconHrefAsync(page)).Should().Be("/static/NodeTypeIcons/document.svg",
            "a page with no icon of its own reads as its NodeType, not as the portal");

        // An IN-CIRCUIT navigation (no page load): the tab icon has to follow the page, the same way
        // the tab title already does.
        await page.EvaluateAsync("path => Blazor.navigateTo(path, false)", $"/{glyphPath}");

        var glyphIcon = await WaitForIconAsync(page, "image/svg+xml");
        Uri.UnescapeDataString(glyphIcon).Should().Contain("🎯",
            "the node's authored glyph is drawn into the tab icon, and it swapped without a page load");
    }

    /// <summary>Waits until the resolved tab icon contains <paramref name="expected"/>, then returns it.</summary>
    private static async Task<string> WaitForIconAsync(IPage page, string expected)
    {
        await page.WaitForFunctionAsync(
            """
            expected => {
              const links = [...document.querySelectorAll('link[rel~="icon"]')];
              const last = links[links.length - 1];
              return !!last && (last.getAttribute('href') || '').includes(expected);
            }
            """,
            expected,
            new PageWaitForFunctionOptions { Timeout = 60_000 });
        return await IconHrefAsync(page);
    }

    private static async Task<string> IconHrefAsync(IPage page) =>
        await page.EvaluateAsync<string>(LastIconHrefJs) ?? "";

    /// <summary>Seeds a markdown page in the signed-in user's writable partition; returns its path.</summary>
    private async Task<string> SeedAsync(IBrowserContext context, string token, string name, string? icon)
    {
        var id = $"tab{Guid.NewGuid():N}"[..14];
        var partition = fixture.UserPartition;
        var path = $"{partition}/{id}";
        var iconField = icon is null ? "" : $"""
              "icon": "{icon}",
            """;
        await fixture.CreateNodeAsync(context, token, $$"""
            {
              "id": "{{id}}",
              "namespace": "{{partition}}",
              "name": "{{name}}",
              "nodeType": "Markdown",
              "mainNode": "{{path}}",
            {{iconField}}
              "content": { "$type": "MarkdownContent", "content": "# Tab icon (e2e)\n\nA page whose tab shows its own icon.\n" }
            }
            """);
        return path;
    }
}
