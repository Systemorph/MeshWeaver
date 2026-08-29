using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Overview and Thumbnail views for individual Activity nodes.
/// Registered via ActivityNodeType's AddActivityViews().
/// </summary>
public static class ActivityLayoutAreas
{

    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";

    /// <summary>Area name for the Thumbnail layout area.</summary>
    public const string ThumbnailArea = "Thumbnail";

    /// <summary>Area name for the Cancel layout area.</summary>
    public const string CancelArea = "Cancel";

    /// <summary>Area name for the Progress layout area.</summary>
    public const string ProgressArea = "Progress";

    /// <summary>
    /// Area id of the script RESULT inside <c>ActivityViews.Progress</c> / <c>ActivityViews.Overview</c> —
    /// the control a script returned, rendered live. Named (not auto-numbered) so the
    /// indicator and log keep their positions and so tests address it by name.
    /// </summary>
    public const string ResultArea = "Result";
}
