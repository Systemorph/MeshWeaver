using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for Code nodes.
/// - Content (default): the notebook cell, stacked CODE → TOOLBAR → OUTPUT (the output appears
///   only once there IS a run to show). For a viewer holding
///   Update the code segment IS an inline Monaco editor with code completion (edit mode is the
///   mode — no Edit button, auto-saved, Run persists the buffer first); for everyone else it is
///   the read-only markdown code block. Run sits directly under the code it executes and directly
///   above the result it produced, and all three segments span the frame's full width.
/// - Overview: Splitter with sibling code list and embedded content view
/// - Edit: Monaco editor with language support (kept for deep links and metadata edits)
/// </summary>
public static class CodeLayoutAreas
{

    /// <summary>Area name for the Content layout area.</summary>
    public const string ContentArea = "Content";

    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";

    /// <summary>Area name for the Edit layout area.</summary>
    public const string EditArea = "Edit";

    /// <summary>Area id of the notebook-cell frame inside the Content area.</summary>
    public const string CellArea = "CodeCell";

    /// <summary>Area id of the cell toolbar (Run / Cancel / Edit + metadata) inside the cell frame.</summary>
    public const string CellToolbarArea = "CellToolbar";

    /// <summary>Area id of the code segment inside the cell frame.</summary>
    public const string CellCodeArea = "CellCode";

    /// <summary>Area id of the output segment (last run's Progress embed) inside the cell frame.</summary>
    public const string CellOutputArea = "CellOutput";

    /// <summary>Area id of the Run button inside the cell toolbar.</summary>
    public const string RunButtonArea = "Run";

    /// <summary>Area id of the "code changed — re-run" chip inside the cell toolbar.</summary>
    public const string StaleChipArea = "StaleChip";

    /// <summary>Area id of the toolbar's execution-state chip (Running… / ✓ Done / ✗ Failed).</summary>
    public const string StatusChipArea = "StatusChip";

    /// <summary>Area id of the Cancel button inside the cell toolbar.</summary>
    public const string CancelButtonArea = "Cancel";

    /// <summary>Area id of the Edit button inside the cell toolbar.</summary>
    public const string EditButtonArea = "Edit";

    /// <summary>Area id of the Cancel button inside the copy-to-home dialog.</summary>
    public const string CopyDialogCancelArea = "CopyDialogCancel";

    /// <summary>Area id of the Confirm button inside the copy-to-home dialog.</summary>
    public const string CopyDialogConfirmArea = "CopyDialogConfirm";

    /// <summary>
    /// Data id of the EDIT-MODE cell's code buffer — what the inline editor binds. Seeded ONCE
    /// per rendered area from the node's stored code (see <c>CodeViews.Content</c>) and written by
    /// the editor from then on; Run snapshots it so the kernel always executes what the viewer
    /// sees.
    /// </summary>
    public const string CellBufferDataId = "cellCode";


}
