using System.Linq;
using MeshWeaver.Documentation.GUI;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Pins the drag-and-drop board example from the GUI documentation
/// (<c>Doc/GUI/DragAndDrop</c>). The example is built from the generic
/// <see cref="DraggableControl"/> / <see cref="DropTargetControl"/> primitives; these
/// tests exercise the exact <see cref="DragDropExample.Move"/> reducer the drop handlers
/// run and the <see cref="DragDropExample.Board"/> composition the doc renders, so the
/// page's example is guaranteed to behave as documented.
/// </summary>
public class DragDropExampleTest
{
    // A single immutable instance: Move returns this same reference on a no-op, so the
    // "unchanged" assertions below compare reference-equal states (BoardState's dictionary
    // uses reference equality, so two freshly-built states would not compare equal).
    private static readonly DragDropExample.BoardState Initial = DragDropExample.BoardState.Initial;

    // ── The reducer: what a column's drop handler runs ──────────────────

    [Fact]
    public void Move_RelocatesCardToTargetColumn()
    {
        var moved = DragDropExample.Move(Initial, "Design API", "Done");

        moved.ColumnOf("Design API").Should().Be("Done");
        moved.Columns["To Do"].Should().NotContain("Design API");
        moved.Columns["Done"].Should().ContainSingle().Which.Should().Be("Design API");
    }

    [Fact]
    public void Move_AppendsToTheEndOfTheTargetColumn()
    {
        var state = DragDropExample.Move(Initial, "Design API", "Done");
        state = DragDropExample.Move(state, "Ship it", "Done");

        state.Columns["Done"].Should().Equal("Design API", "Ship it");
    }

    [Fact]
    public void Move_ToSameColumn_IsANoOp()
    {
        var moved = DragDropExample.Move(Initial, "Design API", "To Do");

        moved.Should().Be(Initial);
        moved.Columns["To Do"].Should().Equal("Design API", "Write tests", "Ship it");
    }

    [Fact]
    public void Move_UnknownColumn_LeavesStateUnchanged()
    {
        var moved = DragDropExample.Move(Initial, "Design API", "Archive");

        moved.Should().Be(Initial);
    }

    [Fact]
    public void Move_UnknownCard_LeavesStateUnchanged()
    {
        var moved = DragDropExample.Move(Initial, "Nonexistent", "Done");

        moved.Should().Be(Initial);
    }

    [Fact]
    public void Initial_HasThreeCardsInToDoAndEmptyElsewhere()
    {
        Initial.Columns["To Do"].Should().Equal("Design API", "Write tests", "Ship it");
        Initial.Columns["In Progress"].Should().BeEmpty();
        Initial.Columns["Done"].Should().BeEmpty();
        Initial.ColumnOf("Write tests").Should().Be("To Do");
        Initial.ColumnOf("missing").Should().BeNull();
    }

    // ── The composition the doc renders ─────────────────────────────────

    [Fact]
    public void Board_RendersOneDropTargetPerColumn()
    {
        var board = DragDropExample.Board(Initial, (_, _) => { });

        var stack = board.Should().BeOfType<StackControl>().Subject;
        // One child area per column.
        stack.Areas.Should().HaveCount(DragDropExample.BoardState.ColumnOrder.Count);
        (stack.Skin as LayoutStackSkin)!.Orientation.Should().Be(Orientation.Horizontal);
    }

    // ── The drop wiring: payload → reducer → new state ──────────────────

    [Fact]
    public void DropWiring_MovesTheDroppedCard()
    {
        // Simulates the loop a live area runs: the drop handler receives the dragged
        // card as the payload and folds Move into the board state.
        var state = Initial;
        void OnDrop(string card, string column) => state = DragDropExample.Move(state, card, column);

        // Build the board so the closures capture OnDrop (mirrors the rendered example).
        _ = DragDropExample.Board(state, OnDrop);

        OnDrop("Write tests", "In Progress");
        OnDrop("Write tests", "Done");

        state.ColumnOf("Write tests").Should().Be("Done");
        state.Columns["To Do"].Should().Equal("Design API", "Ship it");
    }
}
