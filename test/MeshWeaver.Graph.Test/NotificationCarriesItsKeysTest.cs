using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A bell row stores the KEY, and the bell renders it in the VIEWER's language</b> (#4373).
///
/// <para>Every notification the platform raises comes from a background reaction — the
/// package-update reconciler, a module-discovery scan, a startup import — running as SYSTEM with
/// no viewer in scope. Resolving a catalog key there picks the system default (English) and then
/// BAKES it into a durable row that outlives the write by days and is read by several people whose
/// languages differ. So the writer stores the key plus its arguments and the reader resolves.</para>
///
/// <para>These pin the three properties a renderer depends on: an un-keyed row is unchanged, a
/// keyed row resolves per locale, and a key that has LEFT the catalog still renders the sentence
/// the writer meant rather than a raw token.</para>
/// </summary>
public class NotificationCarriesItsKeysTest
{
    private static Notification Plain() => new()
    {
        Title = "Update available: Acme",
        Message = "The plugin repository ships Acme.",
    };

    [Fact]
    public void ARowWithNoKey_RendersItsStoredEnglish_UnchangedInEveryLocale()
    {
        var row = Plain();

        Assert.Equal("Update available: Acme", row.LocalizeTitle("de"));
        Assert.Equal("The plugin repository ships Acme.", row.LocalizeMessage("de"));
        Assert.Equal("Update available: Acme", row.LocalizeTitle((string?)null));
    }

    [Fact]
    public void WithKeys_StoresTheKeyAndItsNamedArguments()
    {
        var row = Plain().WithKeys(
            "notifications.test.title", [("name", "Acme")],
            "notifications.test.body", [("name", "Acme"), ("detail", "")]);

        Assert.Equal("notifications.test.title", row.TitleKey);
        Assert.Equal("Acme", row.TitleArgs!["name"]);
        Assert.Equal("notifications.test.body", row.MessageKey);
        Assert.Equal(2, row.MessageArgs!.Count);
    }

    /// <summary>
    /// The English stays the FALLBACK, so a key the catalog no longer carries — a row written
    /// weeks ago against a template since renamed — renders the writer's sentence, never a bare
    /// <c>notifications.…</c> token in a user's face.
    /// </summary>
    [Fact]
    public void AKeyTheCatalogDoesNotCarry_FallsBackToTheStoredEnglish()
    {
        var row = Plain().WithKeys(
            "notifications.no.such.key.at.all", [("name", "Acme")],
            "notifications.no.such.body.either", [("name", "Acme")]);

        Assert.Equal("Update available: Acme", row.LocalizeTitle("de"));
        Assert.Equal("The plugin repository ships Acme.", row.LocalizeMessage("en"));
    }

    /// <summary>
    /// Arguments are SCALARIZED on the way in. Values round-trip through JSON, so a value read
    /// back is typically a <c>JsonElement</c> rather than the CLR type that was written; storing
    /// only scalars is what keeps template binding stable across that trip.
    /// </summary>
    [Fact]
    public void Arguments_AreScalarized_SoTheySurviveTheJsonRoundTrip()
    {
        var row = Plain().WithKeys("notifications.test.title",
            [("name", new Uri("https://example.invalid/x")), ("count", 3), ("missing", null)]);

        Assert.Equal("https://example.invalid/x", row.TitleArgs!["name"]);
        Assert.Equal(3, row.TitleArgs["count"]);
        Assert.Equal(string.Empty, row.TitleArgs["missing"]);
    }

    /// <summary>
    /// The point of the whole change, on a REAL catalog key: the same stored row renders in the
    /// viewer's language, and the two viewers get different text from one row. Uses
    /// <c>activity.compile.started</c> because it is an existing entry with a NAMED placeholder —
    /// named rather than positional precisely because these arguments are persisted and a
    /// translator must be free to reorder them.
    /// </summary>
    [Fact]
    public void AKeyedRow_RendersDifferentlyForTwoViewers()
    {
        var row = Plain().WithKeys(messageKey: "activity.compile.started", messageArgs: [("path", "Acme/Thing")]);

        var english = row.LocalizeMessage("en");
        var german = row.LocalizeMessage("de");

        Assert.Equal("Compile started for Acme/Thing", english);
        Assert.Contains("Acme/Thing", german);
        Assert.NotEqual(english, german);

        // …and the row it came from is one row: the stored English is untouched.
        Assert.Equal("The plugin repository ships Acme.", row.Message);
    }

    /// <summary>A blank key changes nothing — the caller simply has no catalog entry to offer.</summary>
    [Fact]
    public void ABlankKey_LeavesTheRowAlone()
    {
        var row = Plain().WithKeys(titleKey: "   ", titleArgs: [("name", "Acme")]);

        Assert.Null(row.TitleKey);
        Assert.Null(row.TitleArgs);
    }
}
