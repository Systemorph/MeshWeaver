using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end guard for the chat composer's <c>/</c> slash-skill menu — the exact surface that broke in
/// the atioz demo ("certain users got no auto-expand on skills"). Skills resolve as a per-partition
/// <c>nodeType:Skill</c> union: the platform <c>Skill</c> catalog + the current space's <c>{space}/Skill</c>
/// + the chatting user's <c>{user}/Skill</c> (<see cref="MeshWeaver.AI.AgentPickerProjection.BuildSkillQuery"/>).
/// The per-user/per-space legs are RLS-gated, so they only appear if the GUI forwards the user's
/// AccessContext through the Monaco completion JS-interop hop (where the circuit's AsyncLocal context is
/// nulled). This test drives the REAL Monaco <c>/</c> suggest widget — the only way to exercise that hop.
///
/// <para>It reproduces the atioz setup faithfully: a SECOND user owns the space, and the chatting user is
/// granted <b>Editor</b> (not owner) on it. Then it asserts:
/// <list type="bullet">
///   <item>In the space — the space skill, the user's own skill, AND a built-in all expand;</item>
///   <item>In a DIFFERENT space — the space skill is gone (correctly scoped), the user skill remains.</item>
/// </list>
/// Run on the Colima e2e DevLogin portal: <c>memex-local e2e test SkillSlashMenuScopingTest</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class SkillSlashMenuScopingTest(PortalFixture fixture)
{
    private const string SpaceOwner = "SpaceOwnerE2E";   // a SECOND DevLogin user (self-provisioned)
    private const string Space = "AcmeSkillsE2E";          // the space the chatting user is EDITOR of
    private const string SpaceSkill = "spaceonlyskill";    // /spaceonlyskill — lives in {Space}/Skill
    private const string UserSkill = "useronlyskill";      // /useronlyskill — lives in {user}/Skill
    private const string BuiltInSkill = "agent";           // /agent — a platform built-in (always present)

    [Fact(Timeout = 180_000)]
    public async Task SlashMenu_ShowsSpaceUserAndBuiltinSkills_InSpace_AndScopesSpaceSkillOut_Elsewhere()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        // ── Seed (idempotent) ────────────────────────────────────────────────────────────────────
        // A different user OWNS the space + the space skill; the chatting user is granted EDITOR on it.
        await using (var owner = await fixture.NewAuthenticatedContextAsync(SpaceOwner))
        {
            var ownerToken = await fixture.MintTokenAsync(owner);
            await SeedAsync(owner, ownerToken, $$"""
                { "id": "{{Space}}", "namespace": "", "name": "Acme Skills E2E", "nodeType": "Space",
                  "mainNode": "{{Space}}", "content": { "$type": "Space", "name": "Acme Skills E2E" } }
                """);
            await SeedAsync(owner, ownerToken, $$"""
                { "id": "{{SpaceSkill}}", "namespace": "{{Space}}/Skill", "name": "Space Only Skill",
                  "description": "A skill that lives only in the Acme space.", "nodeType": "Skill",
                  "mainNode": "{{Space}}",
                  "content": { "$type": "SkillDefinition", "instructions": "Do the space thing.", "autoMount": true } }
                """);
            // Grant the chatting user EDITOR (read+write) on the space — NOT owner.
            await SeedAsync(owner, ownerToken, $$"""
                { "id": "{{fixture.UserId}}_Access", "namespace": "{{Space}}/_Access",
                  "name": "{{fixture.UserId}} Access", "nodeType": "AccessAssignment", "mainNode": "{{Space}}",
                  "content": { "$type": "AccessAssignment", "accessObject": "{{fixture.UserId}}",
                    "displayName": "{{fixture.UserId}}",
                    "roles": [ { "role": "Editor", "denied": false } ] } }
                """);
        }

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // The chatting user's OWN skill (their personal {user}/Skill registry).
        await SeedAsync(context, token, $$"""
            { "id": "{{UserSkill}}", "namespace": "{{fixture.UserId}}/Skill", "name": "User Only Skill",
              "description": "A personal skill for {{fixture.UserId}}.", "nodeType": "Skill",
              "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "SkillDefinition", "instructions": "Do the user thing.", "autoMount": true } }
            """);

        // Force the MeshWeaver harness on the chatting user's compose-mode composer. Under a CLI harness
        // (Claude Code / Copilot) the / menu shows the HARNESS's own commands (login/logout/harness), NOT
        // the nodeType:Skill catalog — by design (ThreadChatView.GetCommandCompletions). The skill menu is
        // a MeshWeaver-harness surface, so pin it. The compose-mode composer is {user}/_Thread/ThreadComposer.
        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "MeshWeaver" } }
            """);
        // …and patch it in case it already existed defaulted to a CLI harness (a non-empty Harness is
        // preserved by the composer's default-fill, so this sticks).
        await fixture.PatchNodeAsync(context, token, composerPath, "{\"content\":{\"harness\":\"MeshWeaver\"}}");

        // Access propagation (partition_access sync) is eventually consistent — wait until the chatting
        // user can actually READ the space skill via the API before driving the UI, so a not-yet-propagated
        // grant can't masquerade as the GUI bug.
        var spaceSkillPath = $"{Space}/Skill/{SpaceSkill}";
        (await fixture.WaitUntilReadableAsync(context, token, spaceSkillPath, TimeSpan.FromSeconds(30)))
            .Should().BeTrue($"the Editor grant must propagate so '{spaceSkillPath}' is readable before the UI test");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        // ── In the space: space + user + built-in skills all expand ───────────────────────────────
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{Space}");

        (await SkillRowVisibleAsync(page, SpaceSkill)).Should().BeTrue(
            "the space skill must appear in the / menu when chatting IN the space the user is an Editor of " +
            "(this is the atioz regression: the GUI must forward the user's AccessContext to the RLS-gated " +
            "{space}/Skill read across the Monaco completion JS-interop hop)");
        (await SkillRowVisibleAsync(page, UserSkill)).Should().BeTrue(
            "the user's own {user}/Skill must appear in the / menu");
        (await SkillRowVisibleAsync(page, BuiltInSkill)).Should().BeTrue(
            "a built-in platform skill (/agent) must appear in the / menu");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/skill-menu-in-space.png", FullPage = true });

        // ── In a DIFFERENT space (the user's own home): the SPACE skill is gone, the USER skill stays ──
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        // Positive control first — the / menu works here (the user skill follows the user everywhere)…
        (await SkillRowVisibleAsync(page, UserSkill)).Should().BeTrue(
            "the user's own skill must still appear in a different space");
        // …so the absence of the space skill is real scoping, not a broken menu.
        (await SkillRowVisibleAsync(page, SpaceSkill)).Should().BeFalse(
            "the space-scoped skill must NOT leak into a different space's / menu");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/skill-menu-other-space.png", FullPage = true });
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Navigates to <paramref name="url"/>, opens the side-panel chat, and waits for the Monaco composer.</summary>
    private static async Task OpenComposerOnAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        page.Url.Should().NotContain("/login", "the DevLogin user must stay authenticated");

        // Open the side-panel chat via the layout's "Chat" toggle (same entry point SidePanelChat tests use).
        var toggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await toggle.ClickAsync();

        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        // Let the composer bind its context (nav → ResolveComposerContext) before issuing completions.
        await page.WaitForTimeoutAsync(800);
    }

    /// <summary>
    /// Types <c>/{skillId}</c> into the composer's Monaco editor and reports whether the suggest widget
    /// shows a matching row. Clears the editor first so calls are independent. Returns false (after a
    /// settle wait) when no matching row appears — used for both the positive and the scoping-absent checks.
    /// </summary>
    private static async Task<bool> SkillRowVisibleAsync(IPage page, string skillId)
    {
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        // Close any suggest widget left open by a prior check FIRST (Escape closes it WITHOUT accepting —
        // re-clicking the editor while the suggest overlays it would otherwise accept the highlighted item,
        // add an attachment chip, and pop a "No node found" dialog that blocks the next check).
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        // Clear any prior text (select-all works with Ctrl on win/linux, Cmd on mac via ControlOrMeta).
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");

        await page.Keyboard.TypeAsync($"/{skillId}");

        // The suggest row (Monaco renders matches as .monaco-list-row inside .suggest-widget). The label is
        // the skill's slash word, e.g. "/spaceonlyskill" — match the bare id substring.
        var row = page.Locator(".suggest-widget .monaco-list-row")
            .Filter(new LocatorFilterOptions { HasTextString = skillId });
        bool found;
        try
        {
            await row.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 6_000
            });
            found = true;
        }
        catch (TimeoutException)
        {
            found = false;
            // DIAGNOSTIC: dump what the / menu actually showed so we can tell a real scoping miss from a
            // broken interaction (no suggest widget at all).
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"/tmp/diag-{skillId}.png", FullPage = true });
            var dumped = await page.Locator(".suggest-widget .monaco-list-row").AllInnerTextsAsync();
            var anyWidget = await page.Locator(".suggest-widget").CountAsync();
            var footerEditors = await page.Locator(".thread-chat-footer .monaco-editor").CountAsync();
            await File.AppendAllTextAsync("/tmp/skill-diag.txt",
                $"[{skillId}] suggestWidgets={anyWidget} footerEditors={footerEditors} rows=[{string.Join(" | ", dumped)}]\n");
        }
        // Close the suggest WITHOUT accepting, so the next check starts clean.
        await page.Keyboard.PressAsync("Escape");
        return found;
    }

    private async Task SeedAsync(IBrowserContext context, string token, string nodeJson)
    {
        try { await fixture.CreateNodeAsync(context, token, nodeJson); }
        catch (InvalidOperationException) { /* already seeded from a prior run — fine */ }
    }
}
