using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.Documentation.GUI;

/// <summary>
/// A minimal drag-and-drop board built from the generic <see cref="DraggableControl"/> /
/// <see cref="DropTargetControl"/> primitives — the worked example behind the
/// <c>Doc/GUI/DragAndDrop</c> page. Cards are dragged between columns; the pure
/// <see cref="Move"/> reducer is exactly what a column's drop handler runs, and what
/// <c>DragDropExampleTest</c> pins. The composition in <see cref="Board"/> is what the
/// doc page renders.
/// </summary>
public static class DragDropExample
{
    /// <summary>
    /// The board state: each column name maps to the ordered list of card ids it holds.
    /// Immutable — every move returns a new <see cref="BoardState"/>.
    /// </summary>
    /// <param name="Columns">Card ids by column name.</param>
    public record BoardState(ImmutableDictionary<string, ImmutableList<string>> Columns)
    {
        /// <summary>Column names in display order.</summary>
        public static readonly ImmutableList<string> ColumnOrder = ["To Do", "In Progress", "Done"];

        /// <summary>The initial board: three cards in "To Do", the other columns empty.</summary>
        public static BoardState Initial => new(
            ImmutableDictionary<string, ImmutableList<string>>.Empty
                .Add("To Do", ["Design API", "Write tests", "Ship it"])
                .Add("In Progress", ImmutableList<string>.Empty)
                .Add("Done", ImmutableList<string>.Empty));

        /// <summary>The column currently holding <paramref name="card"/>, or <c>null</c> if none does.</summary>
        /// <param name="card">The card id to locate.</param>
        public string? ColumnOf(string card)
            => Columns.FirstOrDefault(kv => kv.Value.Contains(card)).Key;
    }

    /// <summary>
    /// Moves <paramref name="card"/> to the end of <paramref name="toColumn"/>, removing it from
    /// wherever it currently sits. Pure: dropping a card on a column it already occupies, an unknown
    /// column, or a card that isn't on the board all leave the state unchanged. Returns a new
    /// <see cref="BoardState"/>.
    /// </summary>
    /// <param name="state">The current board.</param>
    /// <param name="card">The dragged card id (the draggable's payload).</param>
    /// <param name="toColumn">The column the card was dropped on.</param>
    public static BoardState Move(BoardState state, string card, string toColumn)
    {
        if (!state.Columns.ContainsKey(toColumn))
            return state;
        var from = state.ColumnOf(card);
        if (from is null || from == toColumn)
            return state;
        var columns = state.Columns
            .SetItem(from, state.Columns[from].Remove(card))
            .SetItem(toColumn, state.Columns[toColumn].Add(card));
        return state with { Columns = columns };
    }

    /// <summary>
    /// Renders the board: a horizontal stack of columns, each a <see cref="DropTargetControl"/> whose
    /// drop handler moves the dropped card (carried as the draggable's payload) into that column via
    /// <paramref name="onDrop"/>; each card is a <see cref="DraggableControl"/> carrying its id as the
    /// payload. In a live area <paramref name="onDrop"/> would fold <see cref="Move"/> into the area's
    /// state stream and re-render.
    /// </summary>
    /// <param name="state">The board to render.</param>
    /// <param name="onDrop">Invoked with (card id, target column) when a card is dropped on a column.</param>
    public static UiControl Board(BoardState state, Action<string, string> onDrop)
    {
        var board = Controls.Stack.WithSkin(s => s.WithOrientation(Orientation.Horizontal));
        foreach (var column in BoardState.ColumnOrder)
        {
            var cards = Controls.Stack;
            foreach (var card in state.Columns[column])
                cards = cards.WithView(Controls.Draggable(Controls.Html($"<div class=\"card\">{card}</div>"), card));

            var target = column; // capture per iteration for the closure
            var zone = Controls.DropTarget(
                    Controls.Stack
                        .WithView(Controls.Html($"<h4>{column}</h4>"))
                        .WithView(cards))
                .WithDropAction(context => onDrop((string)context.Payload!, target));

            board = board.WithView(zone);
        }
        return board;
    }
}
