using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The step-up pages run under a <c>script-src</c> without <c>'unsafe-inline'</c>
/// (<c>Doc/Architecture/ContentSecurityPolicy</c>). They are rendered outside the Blazor shell, so
/// no per-request nonce reaches them: their behaviour is a same-origin FILE
/// (<see cref="StepUpPages.ScriptSource"/>, served at <see cref="StepUpPages.ScriptPath"/>) and
/// their per-page values are a JSON data block the browser never executes. Under the strict policy
/// an inline script on these pages would simply not run — the passkey button would do nothing —
/// so every page shape is scanned for an executable inline script and for an inline event handler.
/// The negative control runs the same scanner over the shape these pages used to emit.
/// </summary>
public class StepUpPagesCarryNoInlineScriptTest
{
    private static readonly StepUpPageTexts English = new("en");

    private static StepUpFactors PasskeyOnly() => new()
    {
        UserId = "u",
        Passkeys = ImmutableDictionary<string, PasskeyCredential>.Empty.Add("AAAA",
            new PasskeyCredential { CredentialId = "AAAA", PublicKey = "", CreatedAt = DateTimeOffset.UnixEpoch }),
    };

    /// <summary>A receipt id that tries to close the data block it is serialized into.</summary>
    private const string HostileReceipt = "r1</script><script>alert(1)</script>";

    public static TheoryData<string, string> Pages() => new()
    {
        { "passkey", StepUpPages.Passkey(English, "/auth/step-up/enroll") },
        { "totp", StepUpPages.Totp(English, "/auth/step-up/totp", "/auth/step-up/enroll") },
        { "message", StepUpPages.Message(English, "Refused.", "/") },
        { "enroll-needed", StepUpPages.EnrollNeeded(English, "/auth/step-up/enroll") },
        { "enroll", StepUpPages.Enroll(English, PasskeyOnly(), mayEnrollTotp: true, HostileReceipt, null, false, "/") },
        { "enroll-confirm-first", StepUpPages.Enroll(English, null, false, null, "/confirm", false, "/") },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void NoPageCarriesAnExecutableInlineScriptOrAnInlineHandler(string page, string html)
    {
        Assert.True(InlineScriptViolations(html).Count == 0,
            $"{page}: {string.Join(" | ", InlineScriptViolations(html))}");
    }

    [Fact]
    public void ThePassKeyPageLoadsTheScriptFileAndNamesItsBehaviour()
    {
        var html = StepUpPages.Passkey(English, "/auth/step-up/enroll");
        Assert.Contains($"<script src=\"{StepUpPages.ScriptPath}\"></script>", html);
        Assert.Equal("passkey", Data(html).GetProperty("page").GetString());
        Assert.False(string.IsNullOrEmpty(Data(html).GetProperty("texts").GetProperty("working").GetString()));
    }

    [Fact]
    public void TheEnrollPageCarriesItsReceiptAsDataThatCannotCloseTheBlock()
    {
        var html = StepUpPages.Enroll(English, PasskeyOnly(), true, HostileReceipt, null, false, "/");
        var data = Data(html);
        Assert.Equal("enroll", data.GetProperty("page").GetString());
        // The value survives as DATA, byte for byte…
        Assert.Equal(HostileReceipt, data.GetProperty("values").GetProperty("receipt").GetString());
        // …and the page holds exactly the two script elements it means to: the data block and the file.
        Assert.Equal(2, Regex.Matches(html, "<script", RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public void ThePageScriptReadsTheDataBlockAndRunsBothPages()
    {
        Assert.Contains($"document.getElementById('{StepUpPages.DataElementId}')", StepUpPages.ScriptSource);
        Assert.Contains("D.page === 'passkey'", StepUpPages.ScriptSource);
        Assert.Contains("D.page === 'enroll'", StepUpPages.ScriptSource);
        Assert.Contains("navigator.credentials.get", StepUpPages.ScriptSource);
        Assert.Contains("navigator.credentials.create", StepUpPages.ScriptSource);
    }

    [Fact]
    public void TheScriptIsServedAnonymouslyAtTheAdvertisedPath()
    {
        var action = typeof(StepUpController).GetMethod(nameof(StepUpController.PageScript))!;
        var route = typeof(StepUpController).GetCustomAttribute<RouteAttribute>()!.Template;
        var get = action.GetCustomAttribute<HttpGetAttribute>()!.Template;
        Assert.Equal(StepUpPages.ScriptPath, "/" + route + "/" + get);
        Assert.NotNull(action.GetCustomAttribute<AllowAnonymousAttribute>());
        var result = new StepUpController(null!, null!, null!, null!, null!, null!).PageScript();
        Assert.Equal(StepUpPages.ScriptSource, result.Content);
        Assert.StartsWith("text/javascript", result.ContentType);
    }

    /// <summary>
    /// Negative control: the scanner is the instrument every assertion above rests on, so it must
    /// FIND the shapes it is there to find — the inline block these pages used to emit, an inline
    /// handler and a <c>javascript:</c> URL.
    /// </summary>
    [Fact]
    public void TheScannerFindsAnInlineScriptAHandlerAndAJavascriptUrl()
    {
        Assert.Single(InlineScriptViolations("<p>x</p><script>const T = {};</script>"));
        Assert.Single(InlineScriptViolations("<button id=\"go\" onclick=\"go()\">Go</button>"));
        Assert.Single(InlineScriptViolations("<a href=\"javascript:void(0)\">x</a>"));
        Assert.Empty(InlineScriptViolations("<script type=\"application/json\" id=\"d\">{}</script><script src=\"/a.js\"></script>"));
    }

    /// <summary>
    /// Negative control for the attribute parse: a name that merely CONTAINS <c>src</c> or
    /// <c>type</c> is not that attribute. An inline script carrying <c>data-src</c> has no
    /// <c>src</c>, and one carrying <c>data-type="application/json"</c> is still executable — both
    /// must be reported. Likewise <c>data-onclick</c> is not a handler and <c>data-href</c> is not a URL.
    /// </summary>
    [Fact]
    public void LookalikeAttributeNamesAreNotTheAttributes()
    {
        Assert.Single(InlineScriptViolations("<script data-src=\"/a.js\">run()</script>"));
        Assert.Single(InlineScriptViolations("<script data-type=\"application/json\">run()</script>"));
        Assert.Single(InlineScriptViolations("<script data-src=\"/a.js\" data-type=\"application/json\">run()</script>"));
        Assert.Empty(InlineScriptViolations("<div data-onclick=\"x\" data-href=\"javascript:x\">x</div>"));
        // …and the exact names still count, in any case and quoting.
        Assert.Empty(InlineScriptViolations("<script SRC='/a.js'></script><script type=application/json>{}</script>"));
    }

    private static JsonElement Data(string html)
    {
        var match = Regex.Match(html,
            $"<script type=\"application/json\" id=\"{StepUpPages.DataElementId}\">(.*?)</script>",
            RegexOptions.Singleline);
        Assert.True(match.Success, "the page carries no data block");
        return JsonDocument.Parse(match.Groups[1].Value).RootElement;
    }

    /// <summary>
    /// Every construct a <c>script-src</c> without <c>'unsafe-inline'</c> refuses: a <c>&lt;script&gt;</c>
    /// with no <c>src</c> whose type the browser executes, an <c>on*</c> attribute, and a
    /// <c>javascript:</c> URL. Attributes are parsed per tag and compared by EXACT name, so
    /// <c>data-src</c> is not <c>src</c>, <c>data-type</c> is not <c>type</c> and
    /// <c>data-onclick</c> is not a handler.
    /// </summary>
    internal static IReadOnlyList<string> InlineScriptViolations(string html)
    {
        var found = new List<string>();
        foreach (Match tag in Regex.Matches(html, @"<([a-zA-Z][a-zA-Z0-9-]*)((?:\s+[^\s=/>]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*/?>"))
        {
            var name = tag.Groups[1].Value;
            var attributes = Attributes(tag.Groups[2].Value);
            if (name.Equals("script", StringComparison.OrdinalIgnoreCase)
                && !attributes.ContainsKey("src")
                && IsExecutableType(attributes.TryGetValue("type", out var type) ? type : ""))
                found.Add("inline <script" + tag.Groups[2].Value + ">");
            foreach (var (attribute, value) in attributes)
            {
                if (attribute.Length > 2 && attribute.StartsWith("on", StringComparison.Ordinal))
                    found.Add("inline handler " + attribute);
                if (attribute is "href" or "src" or "action" or "formaction"
                    && System.Net.WebUtility.HtmlDecode(value).TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    found.Add("javascript: URL in " + attribute);
            }
        }
        return found;
    }

    /// <summary>One tag's attributes, by lower-cased exact name (the first occurrence wins, as in HTML).</summary>
    private static Dictionary<string, string> Attributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match a in Regex.Matches(text, @"([^\s=/>]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?"))
        {
            var key = a.Groups[1].Value.ToLowerInvariant();
            var value = a.Groups[2].Success ? a.Groups[2].Value
                : a.Groups[3].Success ? a.Groups[3].Value
                : a.Groups[4].Value;
            attributes.TryAdd(key, value);
        }
        return attributes;
    }

    private static bool IsExecutableType(string type) =>
        type.Length == 0
        || type.Equals("module", StringComparison.OrdinalIgnoreCase)
        || type.Equals("importmap", StringComparison.OrdinalIgnoreCase)
        || type.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || type.Contains("ecmascript", StringComparison.OrdinalIgnoreCase);
}
