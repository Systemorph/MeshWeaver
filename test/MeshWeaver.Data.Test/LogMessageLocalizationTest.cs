using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the render-time seam for activity transcripts (#3236): a <see cref="LogMessage"/> carries a
/// catalog KEY plus its named arguments and is resolved in the language of whoever READS it, while
/// <see cref="LogMessage.Message"/> stays the English fallback.
///
/// <para>The half that is easy to get wrong, and therefore the half most of this file is about, is
/// the FALLBACK behaviour. Three populations must keep rendering exactly as they did before the
/// seam existed, or the change is a regression dressed as a feature:</para>
/// <list type="number">
///   <item>every row persisted before #3236 — no key at all;</item>
///   <item>every writer that is still un-migrated — also no key;</item>
///   <item>a row whose key has since been renamed or removed from the catalog.</item>
/// </list>
///
/// <para>The other half is the ROUND TRIP. These arguments are persisted, so they come back from
/// storage as <see cref="JsonElement"/> rather than the CLR types that were written; a renderer that
/// cast them would produce the silent null AGENTS.md bans. Pure-function tests — no hub, no IO.</para>
/// </summary>
public class LogMessageLocalizationTest
{
    // A key that really is in the shipped catalogs, with a known German rendering, so this file
    // measures the live catalog rather than a fixture of its own.
    private const string RealKey = "activity.delete.notFound";

    [Fact]
    public void AnUnkeyedMessage_RendersItsStoredEnglish_InEveryLanguage()
    {
        var message = new LogMessage("Roslyn said something only Roslyn can say.", LogLevel.Error);

        Assert.Null(message.MessageKey);
        Assert.Equal(message.Message, message.Localize("en"));
        Assert.Equal(message.Message, message.Localize("de"));
        Assert.Equal(message.Message, message.Localize((string?)null));
    }

    [Fact]
    public void AKeyedMessage_ResolvesInTheViewersLanguage_AndBindsItsNamedArguments()
    {
        var message = new LogMessage("Node not found at path: Acme/Thing", LogLevel.Error)
            .WithKey(RealKey, ("path", "Acme/Thing"));

        var english = message.Localize("en");
        var german = message.Localize("de");

        Assert.Contains("Acme/Thing", english);
        Assert.Contains("Acme/Thing", german);
        // The whole point: two viewers of the SAME stored row see two different sentences.
        Assert.NotEqual(english, german);
        // …and the English rendering is the sentence the writer stored beside the key, so the
        // fallback and the catalog cannot drift apart unnoticed.
        Assert.Equal(message.Message, english);
    }

    /// <summary>
    /// Region variants fold onto the shipped language exactly as everywhere else, and an unshipped
    /// language degrades to English rather than to a blank line.
    /// </summary>
    [Theory]
    [InlineData("de-CH")]
    [InlineData("de_DE")]
    [InlineData("de-AT")]
    public void RegionVariantsResolveToTheShippedLanguage(string requested)
    {
        var message = new LogMessage("Node not found at path: X", LogLevel.Error)
            .WithKey(RealKey, ("path", "X"));

        Assert.Equal(message.Localize("de"), message.Localize(requested));
    }

    [Fact]
    public void AnUnshippedLanguage_FallsBackToEnglish()
    {
        var message = new LogMessage("Node not found at path: X", LogLevel.Error)
            .WithKey(RealKey, ("path", "X"));

        Assert.Equal(message.Localize("en"), message.Localize("fr"));
    }

    /// <summary>
    /// 🚨 The case that makes removing or renaming an <c>activity.*</c> key SAFE. A row persisted
    /// months ago may name a key nobody kept; it must render the English sentence it was written
    /// with, never a raw <c>activity.…</c> token in the middle of a transcript.
    /// </summary>
    [Fact]
    public void AKeyTheCatalogNoLongerHas_RendersTheStoredEnglishFallback()
    {
        var message = new LogMessage("Imported 7 nodes.", LogLevel.Information)
            .WithKey("activity.thisKeyWasRemovedInAlaterRelease", ("count", 7));

        Assert.Equal("Imported 7 nodes.", message.Localize("de"));
        Assert.Equal("Imported 7 nodes.", message.Localize("en"));
    }

    /// <summary>
    /// The arguments are PERSISTED, so what a renderer sees is JSON, not the CLR values the writer
    /// passed. Serialize and deserialize before rendering — this is the shape every real read takes,
    /// and the one a cast would break silently.
    /// </summary>
    [Fact]
    public void ArgumentsSurviveTheJsonRoundTrip_AndStillBind()
    {
        var written = new LogMessage(
                "Delete of 'Acme/Thing' was cancelled after removing 3 node(s) — "
                + "the subtree is left partially deleted.",
                LogLevel.Error)
            .WithKey("activity.delete.cancelledPartial", ("path", "Acme/Thing"), ("count", 3));

        var json = JsonSerializer.Serialize(written);
        var read = JsonSerializer.Deserialize<LogMessage>(json)!;

        Assert.Equal("activity.delete.cancelledPartial", read.MessageKey);
        // The values came back as JsonElement — the renderer must handle that WITHOUT a cast.
        Assert.NotNull(read.MessageArgs);
        Assert.Equal(2, read.MessageArgs!.Count);

        foreach (var locale in new[] { "en", "de" })
        {
            var rendered = read.Localize(locale);
            Assert.Contains("Acme/Thing", rendered);
            Assert.Contains("3", rendered);
            Assert.DoesNotContain("{path}", rendered);
            Assert.DoesNotContain("{count}", rendered);
            Assert.Equal(written.Localize(locale), rendered);
        }
    }

    /// <summary>
    /// 🚨 An argument that is a DOMAIN OBJECT must persist as the text the English fallback shows,
    /// not as JSON. The fallback is built by interpolation — <c>value.ToString()</c> — but an object
    /// handed straight to <c>MessageArgs</c> serialises as a JSON object and comes back as a
    /// <see cref="JsonElement"/>, which renders as raw JSON. The same stored row would then read
    /// <c>… Space:acme …</c> in English and <c>… {"Owner":…} …</c> in German.
    ///
    /// <para>Caught by the automatic review on #3282: two sites passed <c>StreamIdentity</c>, a
    /// record whose <c>ToString()</c> is <c>Owner:Partition</c>. <c>WithKey</c> now reduces such a
    /// value at the SEAM, so the next site cannot reintroduce it — which is what this test pins.</para>
    /// </summary>
    [Fact]
    public void ADomainObjectArgumentPersistsAsItsToString_NotAsJson()
    {
        var identity = new StreamIdentityLike("Space", "acme");
        Assert.Equal("Space:acme", identity.ToString());

        var written = new LogMessage($"Update of {identity} failed: boom", LogLevel.Error)
            .WithKey("activity.dataUpdate.streamUpdateFailed",
                ("stream", identity), ("error", "boom"));

        // Reduced at the seam, BEFORE it can reach the serializer.
        Assert.Equal("Space:acme", written.MessageArgs!["stream"]);

        var read = JsonSerializer.Deserialize<LogMessage>(JsonSerializer.Serialize(written))!;
        foreach (var locale in new[] { "en", "de" })
        {
            Assert.Contains("Space:acme", read.Localize(locale));
            Assert.DoesNotContain("Owner", read.Localize(locale));
            Assert.DoesNotContain("{", read.Localize(locale));
        }

        Assert.Equal(written.Message, written.Localize("en"));
    }

    /// <summary>
    /// The other half of the same rule: a JSON scalar is kept AS a value, so the renderer can still
    /// format it in the viewer's culture rather than being handed a pre-stringified server-culture
    /// rendering.
    /// </summary>
    [Fact]
    public void JsonScalarArgumentsAreKeptAsValues()
    {
        var m = new LogMessage("x", LogLevel.Information)
            .WithKey("activity.delete.cancelledPartial", ("path", "p"), ("count", 3));

        Assert.IsType<string>(m.MessageArgs!["path"]);
        Assert.IsType<int>(m.MessageArgs["count"]);
    }

    /// <summary>A record with a custom <c>ToString</c>, shaped like the real <c>StreamIdentity</c>.</summary>
    private sealed record StreamIdentityLike(string Owner, string? Partition)
    {
        public override string ToString() => Partition is null ? Owner : $"{Owner}:{Partition}";
    }

    /// <summary>
    /// A blank key is a no-op rather than a throw: an activity transcript must never be the thing
    /// that takes down the work it records.
    /// </summary>
    [Fact]
    public void ABlankKeyLeavesTheMessageUntouched()
    {
        var original = new LogMessage("Something happened.", LogLevel.Information);

        Assert.Null(original.WithKey("").MessageKey);
        Assert.Null(original.WithKey("   ").MessageKey);
        Assert.Equal("Something happened.", original.WithKey("").Localize("de"));
    }

    [Fact]
    public void ANullArgumentBindsAsEmpty_RatherThanThrowing()
    {
        var message = new LogMessage("Node not found at path: ", LogLevel.Error)
            .WithKey(RealKey, ("path", null));

        Assert.NotNull(message.MessageArgs);
        Assert.Equal(string.Empty, message.MessageArgs!["path"]);
        Assert.DoesNotContain("{path}", message.Localize("de"));
    }

    /// <summary>
    /// An argument the template does not name is simply unused, and a placeholder no argument
    /// supplies stays VISIBLE as <c>{name}</c> — a gap in a transcript should read as a gap, not as
    /// text that was never there.
    /// </summary>
    [Fact]
    public void AnUnsuppliedPlaceholderStaysVisible()
    {
        var message = new LogMessage("Node not found at path: X", LogLevel.Error)
            .WithKey(RealKey, ("unrelated", "value"));

        Assert.Contains("{path}", message.Localize("en"));
    }

    /// <summary>
    /// 🚨 The two placeholder conventions must not collide. The <c>activity.*</c> keys use NAMED
    /// placeholders precisely because their arguments are persisted; the ~1170 keys that predate
    /// them use positional <c>{0}</c>, and the named binder must leave those completely alone —
    /// otherwise adding this seam would have quietly rewritten every existing translated string.
    /// </summary>
    [Fact]
    public void TheNamedBinderLeavesPositionalTemplatesAlone()
    {
        // A real positional key from the shipped catalog.
        var positional = LocalizationCatalog.Get("redirect.notice", "en", "Old/Path");
        Assert.Contains("Old/Path", positional);

        var viaNamed = LocalizationCatalog.GetNamed(
            "redirect.notice", "en",
            ImmutableDictionary<string, object>.Empty.Add("path", "Old/Path"));
        Assert.Contains("{0}", viaNamed);
        Assert.DoesNotContain("Old/Path", viaNamed);
    }

    /// <summary>
    /// The roll-ups <see cref="ActivityLog"/> keeps are severity- and count-based, so keying a
    /// message must not disturb them — <see cref="LogMessage.WithKey"/> is a <c>with</c>-copy and
    /// nothing more.
    /// </summary>
    [Fact]
    public void KeyingDoesNotDisturbTheRestOfTheRecord()
    {
        var original = new LogMessage("text", LogLevel.Warning)
        {
            CategoryName = "Cat",
            Scopes = [new("k", "v")],
        };
        var keyed = original.WithKey(RealKey, ("path", "p"));

        Assert.Equal(original.Message, keyed.Message);
        Assert.Equal(original.LogLevel, keyed.LogLevel);
        Assert.Equal(original.Timestamp, keyed.Timestamp);
        Assert.Equal(original.CategoryName, keyed.CategoryName);
        Assert.Same(original.Scopes, keyed.Scopes);

        var log = new ActivityLog("Test").Append(keyed);
        Assert.Equal(LogLevel.Warning, log.MaxSeverity);
        Assert.Equal(1, log.MessageCount);
    }
}
