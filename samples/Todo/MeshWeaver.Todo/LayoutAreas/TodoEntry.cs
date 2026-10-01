using System.Collections.Immutable;
using MeshWeaver.Todo.Domain;

namespace MeshWeaver.Todo.LayoutAreas;

/// <summary>
/// One row of a Todo view's bound list — the PROJECTION every Todo area feeds into its template
/// (Doc/GUI/DataBinding → "Templates first, data later"). A row is either a heading line (a section
/// title, a summary line, an empty state), optionally with a group action, or a todo item with its
/// per-item actions; the parts a row does not have are hidden by their bound style, so the template's
/// shape never depends on data.
///
/// <para>Its actions are ROW-SCOPED (Doc/GUI/DataBinding → "Row-scoped actions"): the template's
/// buttons exist once, and each click reads the row it was raised from with
/// <c>ctx.RowAs&lt;TodoEntry&gt;()</c> — the row as the person saw it. A group action therefore acts
/// on <see cref="Group"/> exactly as it was rendered, and an item action on <see cref="Item"/>.</para>
/// </summary>
public record TodoEntry
{
    /// <summary>A key unique within the list (diagnostics and test identity).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Markdown for the heading part; empty for an item row.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>The heading part's style — <see cref="TodoStyles.Hidden"/> on an item row.</summary>
    public string HeadingStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>The group action's label.</summary>
    public string GroupTitle { get; init; } = string.Empty;

    /// <summary>The group action's icon.</summary>
    public string GroupIcon { get; init; } = string.Empty;

    /// <summary>The group action's style — hidden when the row has no group action.</summary>
    public string GroupStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>What the group action does — a <see cref="TodoGroupActions"/> value.</summary>
    public string GroupAction { get; init; } = string.Empty;

    /// <summary>The todos the group action acts on, as rendered.</summary>
    public ImmutableList<TodoItem> Group { get; init; } = [];

    /// <summary>The person a <see cref="TodoGroupActions.AssignFirst"/> assigns to.</summary>
    public string? Person { get; init; }

    /// <summary>The todo of an item row, as rendered; null on a heading row.</summary>
    public TodoItem? Item { get; init; }

    /// <summary>The item card's style — <see cref="TodoStyles.Hidden"/> on a heading row.</summary>
    public string ItemStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>The status glyph shown on the card.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>The card's markdown (title, category, responsible person, due date, description).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>The item action menu's style — hidden on heading rows and on assignment rows.</summary>
    public string ActionsStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>The primary action's label (depends on the item's status).</summary>
    public string PrimaryTitle { get; init; } = string.Empty;

    /// <summary>The primary action's icon.</summary>
    public string PrimaryIcon { get; init; } = string.Empty;

    /// <summary>Style of the "Complete" secondary action (shown while pending).</summary>
    public string CompleteStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>Style of the "Cancel" secondary action (shown while pending or in progress).</summary>
    public string CancelStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>Style of the "Pause" secondary action (shown while in progress).</summary>
    public string PauseStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>Style of the "Send Reminder" secondary action (shown while overdue and open).</summary>
    public string ReminderStyle { get; init; } = TodoStyles.Hidden;

    /// <summary>The assignment menu's style — shown on rows that offer assignment instead of actions.</summary>
    public string AssignStyle { get; init; } = TodoStyles.Hidden;
}

/// <summary>
/// What a heading row's group action does. An OPEN set of string constants
/// (Doc/Architecture/OpenVocabulariesAsStringConstants): an unknown value is logged, never defaulted.
/// </summary>
public static class TodoGroupActions
{
    /// <summary>Moves the group to In Progress.</summary>
    public const string Start = "start";

    /// <summary>Moves the group to Completed.</summary>
    public const string Complete = "complete";

    /// <summary>Deletes the group (archive / delete).</summary>
    public const string Delete = "delete";

    /// <summary>Assigns the group round-robin over the team.</summary>
    public const string AutoAssign = "autoAssign";

    /// <summary>Assigns the group's first todo to <see cref="TodoEntry.Person"/>.</summary>
    public const string AssignFirst = "assignFirst";
}

/// <summary>The inline styles the Todo template binds — a part is shown with its style, or hidden.</summary>
public static class TodoStyles
{
    /// <summary>Hides a part the row does not have.</summary>
    public const string Hidden = "display: none;";

    /// <summary>A menu button aligned to the right of its grid cell.</summary>
    public const string Menu = "display: flex; align-items: center; justify-content: flex-end; height: 100%; padding-top: 20px;";

    /// <summary>A secondary menu entry that is offered.</summary>
    public const string Shown = "";

    /// <summary>The todo card.</summary>
    public const string Card = "display: flex; flex-direction: row; align-items: flex-start; gap: 12px; padding: 12px; margin-bottom: 8px; "
        + "border: 1px solid var(--color-border-default); border-radius: 6px; background-color: var(--color-canvas-subtle); "
        + "box-shadow: 0 1px 2px var(--color-shadow-small);";

    /// <summary>A todo card indented under its section.</summary>
    public const string IndentedCard = Card + " margin-left: 10px;";

    /// <summary>A compact one-line todo (the planning backlog).</summary>
    public const string Line = "padding-left: 20px; margin-bottom: 5px;";

    /// <summary>The cell holding a row's action menu.</summary>
    public const string Actions = "display: flex; justify-content: center; align-items: center; padding: 12px; margin-bottom: 8px;";

    /// <summary>A section heading in <paramref name="color"/>.</summary>
    public static string Heading(string color, string marginTop = "16px")
        => $"margin-top: {marginTop}; margin-bottom: 6px; color: {color};";

    /// <summary>An indented statistics line.</summary>
    public const string Stat = "padding-left: 20px; margin-bottom: 5px;";

    /// <summary>A muted hint.</summary>
    public const string Muted = "color: var(--color-fg-muted);";

    /// <summary>A centred "all caught up" message.</summary>
    public const string Success = "color: var(--color-success-fg); text-align: center; margin-top: 40px;";
}
