using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the read-time seam for the bell (Systemorph/MeshWeaver#4373): a notification stores the
/// catalog KEY plus its named arguments and is resolved in the language of whoever READS the row,
/// while <see cref="Notification.Title"/>/<see cref="Notification.Message"/> stay the English
/// fallback.
///
/// <para>🚨 <b>The write is the half that had to change, and it is the half a pure-function test
/// cannot see.</b> A notification is raised on a background reaction with no viewer, so the key has
/// to survive the WRITE — serialization into the node's content, storage, and the read back — or
/// the row reaching the German viewer still carries only English. Every case here therefore goes
/// through a REAL <see cref="IMeshService"/> (<see cref="MonolithMeshTestBase"/>) and asserts on the
/// node that came BACK, never on the value passed in. That is also what proves
/// <see cref="Notification.TitleArgs"/> round-trips: after storage the values are
/// <c>JsonElement</c>, and a renderer that cast them would produce the silent null AGENTS.md
/// bans.</para>
///
/// <para>The other half most of this file is about is the FALLBACK, because three populations must
/// keep rendering exactly as they did before the seam existed: every row written before #4373, every
/// notification whose text is verbatim upstream output, and a row naming a key that has since left
/// the catalog.</para>
/// </summary>
public class NotificationLocalizationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // A key that really is in the shipped catalogs, with a known German rendering, so this file
    // measures the LIVE catalog rather than a fixture of its own.
    private const string RealKey = "notification.accessGranted.title";

    private async Task<Notification> Write(LocalizableText title, LocalizableText message, string id)
    {
        using (Access.ImpersonateAsSystem())
        {
            var node = await NotificationService.CreateLocalizableNotification(
                    MeshService, $"{TestPartition}/Thing", title, message,
                    NotificationType.System, recipient: "reader", identity: id)
                .Should().Emit();
            return node.ContentAs<Notification>(Mesh.JsonSerializerOptions)!;
        }
    }

    /// <summary>
    /// THE case the issue is about: one stored row, two readers, two languages — and the arguments
    /// bound into both.
    /// </summary>
    [Fact]
    public async Task AKeyedNotification_RendersInTheLanguageOfWhoeverReadsIt()
    {
        var stored = await Write(
            LocalizableText.Keyed("You've been given access to Quarterly Report", RealKey,
                ("name", "Quarterly Report")),
            LocalizableText.Keyed("Import failed during startup.",
                "notification.import.startupFailed.body"),
            id: "localization|keyed");

        stored.TitleKey.Should().Be(RealKey,
            "the key has to survive serialization into the node's content and the read back — "
            + "resolving at write time is exactly what #4373 removed");
        stored.TitleArgs.Should().ContainKey("name");

        var english = stored.LocalizedTitle("en");
        var german = stored.LocalizedTitle("de");

        english.Should().Contain("Quarterly Report");
        german.Should().Contain("Quarterly Report",
            "the argument comes back from storage as a JsonElement and must still bind");
        german.Should().NotBe(english, "that is the whole point — one row, two languages");
        english.Should().Be(stored.Title,
            "the stored English IS what the key renders in English, so the two cannot drift unnoticed");
        stored.LocalizedMessage("de").Should().NotBe(stored.LocalizedMessage("en"));
    }

    /// <summary>
    /// Population 1 and 2 — a row written before #4373, and a writer whose text is verbatim upstream
    /// output no catalog can carry. Both must render their stored English in every language, which
    /// is what makes the seam adoptable one site at a time.
    /// </summary>
    [Fact]
    public async Task AVerbatimNotification_RendersItsStoredEnglish_InEveryLanguage()
    {
        var stored = await Write(
            LocalizableText.Verbatim("Roslyn said something only Roslyn can say."),
            LocalizableText.Verbatim("CS0246: the type could not be found"),
            id: "localization|verbatim");

        stored.TitleKey.Should().BeNull();
        stored.LocalizedTitle("en").Should().Be(stored.Title);
        stored.LocalizedTitle("de").Should().Be(stored.Title);
        stored.LocalizedMessage("de").Should().Be(stored.Message);
    }

    /// <summary>
    /// The string entry point every un-migrated caller (and every already-published module bundle)
    /// still binds must keep producing exactly the old row: text on Title/Message, no key. This is
    /// the assertion that makes <see cref="NotificationService.CreateNotification"/> safe to leave
    /// in place rather than change its signature.
    /// </summary>
    [Fact]
    public async Task TheStringEntryPoint_StillWritesAnUnkeyedRow()
    {
        MeshNode node;
        using (Access.ImpersonateAsSystem())
            node = await NotificationService.CreateNotification(
                    MeshService, $"{TestPartition}/Thing", "Plain title", "Plain body",
                    NotificationType.System, recipient: "reader", identity: "localization|plain")
                .Should().Emit();

        var stored = node.ContentAs<Notification>(Mesh.JsonSerializerOptions)!;
        stored.Title.Should().Be("Plain title");
        stored.TitleKey.Should().BeNull();
        stored.MessageKey.Should().BeNull();
        stored.LocalizedTitle("de").Should().Be("Plain title");
    }

    /// <summary>
    /// 🚨 Population 3, and the case that makes RENAMING or removing a <c>notification.*</c> key
    /// safe: a row persisted months ago may name a key nobody kept. It must render the English
    /// sentence it was written with, never a raw <c>notification.…</c> token in a bell row.
    /// </summary>
    [Fact]
    public async Task AKeyThatHasLeftTheCatalog_FallsBackToTheStoredEnglish()
    {
        var stored = await Write(
            LocalizableText.Keyed("A sentence whose key was renamed away.",
                "notification.gone.title", ("name", "X")),
            LocalizableText.Verbatim("body"),
            id: "localization|orphaned");

        stored.LocalizedTitle("de").Should().Be("A sentence whose key was renamed away.");
        stored.LocalizedTitle("en").Should().Be("A sentence whose key was renamed away.");
    }

    /// <summary>
    /// Region variants fold onto the shipped language, and an unshipped language degrades to English
    /// rather than to a raw key — the same resolution rule the rest of the chrome follows, applied
    /// to a persisted row.
    /// </summary>
    [Theory]
    [InlineData("de-CH")]
    [InlineData("de-AT")]
    public async Task RegionVariantsResolveToTheShippedLanguage(string requested)
    {
        var stored = await Write(
            LocalizableText.Keyed("You've been given access to Thing", RealKey, ("name", "Thing")),
            LocalizableText.Verbatim("body"),
            id: $"localization|region|{requested}");

        stored.LocalizedTitle(requested).Should().Be(stored.LocalizedTitle("de"));
        stored.LocalizedTitle("fr").Should().Be(stored.LocalizedTitle("en"));
    }
}
