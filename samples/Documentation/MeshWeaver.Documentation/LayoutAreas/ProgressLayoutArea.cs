using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using static MeshWeaver.Layout.Controls;

namespace MeshWeaver.Documentation.LayoutAreas;

/// <summary>
/// Demos the usage of progress bars and progress indicators
/// </summary>
public static class ProgressLayoutArea
{
    /// <summary>
    /// Adds the progress layout area to the Documentation Application.
    /// </summary>
    /// <param name="layout"></param>
    /// <returns></returns>
    public static LayoutDefinition AddProgress(this LayoutDefinition layout)
        => layout.WithView(nameof(Progress), Progress);

    private record ProgressSettings(int MillisecondsToTick = 10, int MaxProgress = 100);
    /// <summary>
    /// Creates the main progress demo control containing settings and run button.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>A UI control with progress demo header, settings editor, and run button.</returns>
    private static UiControl Progress(LayoutAreaHost host, RenderingContext ctx)
    {
        return Stack.WithView(H2("Progress Demo").WithStyle(s => s.WithMarginBottom("1em")))
            .WithView(host.Edit(new ProgressSettings(), s => 
                RunButton(host, s)));
    }

    /// <summary>
    /// Creates a button that starts the progress simulation when clicked.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="s">The progress settings configuration.</param>
    /// <returns>A button control configured to start the progress simulation.</returns>
    private static ButtonControl RunButton(LayoutAreaHost host, ProgressSettings s)
    {
        return Button("Run").WithClickAction(c => RenderProgress(host, c.Area, s));
    }

    /// <summary>
    /// Renders and manages the progress bar simulation with real-time updates.
    /// </summary>
    /// <param name="host">The layout area host for updating the UI.</param>
    /// <param name="area">The area identifier where the progress will be rendered.</param>
    /// <param name="progressSettings">Configuration settings for the progress simulation.</param>
    /// <returns>A task representing the asynchronous progress simulation operation.</returns>
    private static async Task RenderProgress(LayoutAreaHost host, string area, ProgressSettings progressSettings)
    {
        var progress = new Subject<int>();
        var message = new Subject<string>();
        var cancellationTokenSource = new CancellationTokenSource();
        progress.Subscribe(p => host.UpdateData(nameof(progress), p));
        message.Subscribe(m => host.UpdateData(nameof(message), m));
        host.UpdateArea(area,
            Controls.Stack
                .WithView(Controls.Progress(new JsonPointerReference(LayoutAreaReference.GetDataPointer(nameof(message))),
                    new JsonPointerReference(LayoutAreaReference.GetDataPointer(nameof(progress))))
                )
                .WithView(Button("Cancel").WithClickAction(_ => cancellationTokenSource.Cancel()))
            );
        message.OnNext("Working...");
        progress.OnNext(0);
        for (var i = 1; i <= progressSettings.MaxProgress && !cancellationTokenSource.IsCancellationRequested; i++)
        {
            await Task.Delay(progressSettings.MillisecondsToTick);
            progress.OnNext(i);
            if (i == 50)
                message.OnNext("Halfway there!");
            if (i == 100)
                message.OnNext("Done!");
        }

        if (cancellationTokenSource.IsCancellationRequested)
        {
                progress.OnNext(0);
                message.OnNext("Cancelled!");
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
        progress.OnCompleted();
        message.OnCompleted();
        host.UpdateArea(area, RunButton(host, progressSettings));
    }
}
