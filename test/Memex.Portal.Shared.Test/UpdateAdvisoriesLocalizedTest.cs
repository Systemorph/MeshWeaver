using System;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Release-availability advisories follow the VIEWER (#6540). The gate reports what it said about a
/// target release (a boot compile, an unmeasured module, a declared floor) on
/// <c>Admin/UpdatePolicy</c>; the Updates tab must render each item through the viewer's localizer.
///
/// <para>The record is built from JSON on purpose: the reproduction states the persisted shape
/// (structured reasons beside the English sentence) without referring to a type that only the fix
/// adds, so it compiles on <c>main</c> and FAILS there by assertion, not by a compile error.</para>
///
/// <para>Negative control: on <c>main</c> the only emission of an advisory item is the verbatim
/// <c>string.Join("; ", content.Advisories.Split("; ").Select(Sanitize))</c> in
/// <c>UpdatePolicySettingsTab.AvailabilityMarkdown</c>; it has no <c>localize</c> call, so the
/// persisted English sentence reaches the markdown unchanged and the first assertion cannot hold.</para>
/// </summary>
public class UpdateAdvisoriesLocalizedTest
{
    private const string Tag = "3.0.0-ci.8100";

    private const string English =
        "would recompile at boot on 3.0.0-ci.8100 (no sealed content bake for framework identity s1): Education, Crm";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Advisories_RenderThroughTheViewersLocalizer_NotVerbatim()
    {
        var content = JsonSerializer.Deserialize<UpdatePolicyContent>(
            $$"""
            {
              "latestAvailableTag": "{{Tag}}",
              "checkedAt": "2026-10-12T07:00:00Z",
              "advisoriesTag": "{{Tag}}",
              "advisories": "{{English}}",
              "advisoryReasons": [
                { "code": "bootCompile", "arguments": ["{{Tag}}", "s1", "Education, Crm"] }
              ]
            }
            """,
            Options)!;

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, DeLocalizer);

        // The reason is localized at render time: the key and its positional arguments reach the
        // viewer's localizer, in the order the code defines (target, identity, packages).
        Assert.Contains($"DE:advisory.bootCompile[{Tag}|s1|Education, Crm]", markdown, StringComparison.Ordinal);
        // ...and the persisted English sentence is not what the viewer reads.
        Assert.DoesNotContain("would recompile at boot", markdown, StringComparison.Ordinal);
        // The heading was already localized and stays so.
        Assert.Contains($"DE:ui.updateAdvisories[{Tag}]", markdown, StringComparison.Ordinal);
    }

    private static string DeLocalizer(string key, object?[] args) =>
        $"DE:{key}[{string.Join('|', args)}]";
}
