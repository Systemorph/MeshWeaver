using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Converging a default app record onto redrawn artwork — and, more importantly, the four things
/// that convergence must refuse to touch.
///
/// <para>Changing a default's icon only ever reaches users who have not been seeded yet; everyone
/// else keeps what their record was stamped with, because nothing revisits a record once it exists.
/// This is the other half of such a change. The danger in writing it is doing too MUCH: a rule of
/// "differs from the current seed" would also overwrite an icon a viewer chose, and would fight the
/// Store, which converges the records it owns. Two writers on one field with overlapping conditions
/// is how a tile starts flickering between two answers on alternate logons.</para>
/// </summary>
public class DefaultAppIconRefreshTest
{
    private const string Current = "<svg viewBox='0 0 48 48'><rect/></svg>";
    private const string Superseded = "/static/NodeTypeIcons/chat.svg";

    private static MeshNode RecordWith(string? icon) =>
        MeshNode.FromPath("alice/_App/Chat") with { NodeType = AppNodeType.NodeType, Icon = icon };

    [Fact]
    public void A_record_still_wearing_a_retired_core_icon_is_refreshed()
    {
        AppIconAdoption.NeedsIconRefresh(RecordWith(Superseded), Current).Should().BeTrue();
    }

    [Fact]
    public void A_record_already_on_the_current_artwork_is_left_alone()
    {
        // Idempotence: EveryLogon means this runs on every sign-in, so the steady state has to be
        // a no-op rather than a rewrite of the same value.
        AppIconAdoption.NeedsIconRefresh(RecordWith(Current), Current).Should().BeFalse();
    }

    [Fact]
    public void An_icon_the_viewer_or_the_Store_chose_is_never_overwritten()
    {
        // 🚨 The property that makes this safe. A value core never shipped is somebody's choice —
        // a viewer's, or the Store's for a package it owns — and is not ours to converge.
        AppIconAdoption.NeedsIconRefresh(RecordWith("<svg id='chosen-by-someone'/>"), Current)
            .Should().BeFalse();
        AppIconAdoption.NeedsIconRefresh(RecordWith("/static/NodeTypeIcons/rocket.svg"), Current)
            .Should().BeFalse();
    }

    [Fact]
    public void A_record_with_no_icon_is_left_to_the_adoption_action()
    {
        // Disjoint responsibilities: NeedsIcon fills a blank, NeedsIconRefresh moves off a retired
        // value. If both matched the same record they would race to write the same field.
        AppIconAdoption.NeedsIconRefresh(RecordWith(null), Current).Should().BeFalse();
        AppIconAdoption.NeedsIconRefresh(RecordWith(""), Current).Should().BeFalse();
        AppIconAdoption.NeedsIcon(RecordWith(null)).Should().BeTrue();
    }

    [Fact]
    public void Nothing_happens_when_there_is_no_current_artwork_to_converge_to()
    {
        // A default that the deployment does not configure has no target value; refreshing toward
        // null would blank a tile that renders fine today.
        AppIconAdoption.NeedsIconRefresh(RecordWith(Superseded), null).Should().BeFalse();
        AppIconAdoption.NeedsIconRefresh(RecordWith(Superseded), "").Should().BeFalse();
    }

    [Fact]
    public void It_runs_every_logon_so_a_later_redraw_is_not_stranded()
    {
        // Run-once would converge whatever was stale on the day it ran and record itself as done,
        // leaving every FUTURE redraw permanently unreachable — the exact bug this action exists
        // to fix, reintroduced one level up.
        new DefaultAppIconRefreshLogonAction().Mode.Should().Be(LogonActionMode.EveryLogon);
    }
}
