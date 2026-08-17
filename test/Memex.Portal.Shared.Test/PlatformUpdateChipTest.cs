using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the header build chip: what it names, what it says, and what a click does.
///
/// <para>The behaviour worth protecting is that the chip renders in EVERY state. It is the answer to
/// "which instance am I on?", which is the question ahead of starting a thread round — and it can
/// only confirm a refresh landed if the running build was already on screen before it. A chip that
/// showed up only when an update was pending would announce availability and never arrival.</para>
/// </summary>
public class PlatformUpdateChipTest
{
    // Localizer echoes the key, so assertions pin WHICH key each state reads.
    private static string Echo(string key) => key;

    private static PlatformUpdateChip Describe(PlatformUpdateStatus status) =>
        PlatformUpdateChip.Describe(status, "3.0.0-rc4.ci.4180", "memex-portal-7d9c-abcde", Echo);

    [Fact]
    public void UpToDate_still_names_the_running_build_and_instance()
    {
        var chip = Describe(new PlatformUpdateStatus(PlatformUpdateAvailability.UpToDate, null));

        chip.DisplayVersion.Should().Be("3.0.0-rc4.ci.4180");
        chip.IsUpdate.Should().BeFalse();
        chip.Action.Should().Be(PlatformUpdateChipAction.OpenAbout,
            "there is no newer build to reload onto, so the chip is a link to the full build identity");
        chip.Tooltip.Should().Contain("3.0.0-rc4.ci.4180").And.Contain("memex-portal-7d9c-abcde",
            "two replicas can run the same build, so the version alone cannot answer 'did my session move?'");
    }

    [Fact]
    public void Unknown_is_not_silent_it_still_shows_the_running_build()
    {
        // Unknown means nothing is polling — so no VERDICT may be claimed. The running build is a
        // fact regardless, and it is the whole point of the chip.
        var chip = Describe(PlatformUpdateStatus.Unknown);

        chip.DisplayVersion.Should().Be("3.0.0-rc4.ci.4180");
        chip.IsUpdate.Should().BeFalse();
        chip.Tooltip.Should().NotContain("about.upToDate",
            "an install with nothing polling must not be told it is up to date");
    }

    [Fact]
    public void UpdateAvailable_names_the_pending_build_and_offers_the_refresh()
    {
        var chip = Describe(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpdateAvailable, "3.0.0-rc4.ci.4191"));

        chip.DisplayVersion.Should().Be("3.0.0-rc4.ci.4191",
            "the pending build is the actionable number, so it takes the visible slot");
        chip.IsUpdate.Should().BeTrue();
        chip.Action.Should().Be(PlatformUpdateChipAction.Refresh);
        chip.Tooltip.Should().Contain("about.updateAvailable").And.Contain("ui.updateRefreshHint");
        chip.Tooltip.Should().Contain("3.0.0-rc4.ci.4180",
            "the running build stays on the tooltip — it is what the new one will be compared against");
    }

    /// <summary>
    /// 🚨 The state that must not collapse into the one beside it. A hold is loud (it reads
    /// differently from an available update, per PlatformUpdateAvailability.UpdateHeld) but NOT
    /// actionable from the header: refreshing cannot clear it, and offering the button would teach
    /// the user to click at a problem they cannot fix from here.
    /// </summary>
    [Fact]
    public void UpdateHeld_says_so_but_offers_no_refresh()
    {
        var chip = Describe(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpdateHeld, "3.0.0-rc4.ci.4191"));

        chip.IsUpdate.Should().BeTrue("a held update is still a pending one — it must not look quiet");
        chip.Tooltip.Should().Contain("about.updateHeld").And.Contain("3.0.0-rc4.ci.4191");
        chip.Tooltip.Should().NotContain("about.updateAvailable",
            "a deployment that has REFUSED a build must not read like one that is about to take it");
        chip.Action.Should().Be(PlatformUpdateChipAction.OpenAbout,
            "a refresh cannot clear a hold");
    }

    [Fact]
    public void Every_localization_key_the_chip_reads_exists_in_the_catalog()
    {
        // A key missing from the catalog fails nowhere at compile time — it renders raw to the user.
        string[] used =
        [
            "about.version", "about.instance", "about.updateAvailable", "about.updateHeld",
            "ui.updateRefreshHint"
        ];

        var missing = used.Where(k => !Catalog("en").ContainsKey(k)).ToList();
        missing.Should().BeEmpty($"the chip renders these keys raw when absent: {string.Join(", ", missing)}");

        // German too: the chip is header chrome on every page, so a gap ships to every German user.
        var untranslated = used.Where(k => !Catalog("de").ContainsKey(k)).ToList();
        untranslated.Should().BeEmpty($"missing German: {string.Join(", ", untranslated)}");
    }

    private static Dictionary<string, string> Catalog(string locale)
    {
        var path = Path.Combine(FindRepoRoot(),
            "src", "MeshWeaver.Messaging.Hub", "Localization", $"strings.{locale}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
