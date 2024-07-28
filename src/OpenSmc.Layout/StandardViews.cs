using OpenSmc.Activities;
using OpenSmc.Application.Styles;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Layout;

public static class StandardViews
{
    public static string ActivityLogViewId = nameof(ActivityLogViewId);
    public static string CloseButtonId = nameof(CloseButtonId);

    public static LayoutStackControl RenderLog(this ActivityLog log)
    {
        IconControl icon = log.Status switch
        {
            ActivityLogStatus.Succeeded
                => !log.Warnings().Any()
                    ? Icon(Icons.OpenSmc.Check, Colors.Success) with
                    {
                        Size = "XL"
                    }
                    : Icon(Icons.OpenSmc.Alert, Colors.Warning) with
                    {
                        Size = "XL"
                    },
            ActivityLogStatus.Failed
                => Icon(Icons.OpenSmc.XCircle, Colors.Failed) with
                {
                    Size = "XL"
                },
            ActivityLogStatus.Cancelled
                => Icon(Icons.OpenSmc.XCircle, Colors.Cancel) with
                {
                    Size = "XL"
                },
            _ => Icon(Icons.OpenSmc.Alert, Colors.Cancel) with { Size = "XL" },
        };

        var shortDateString = log.Start.ToString("HH:MM MMM dd, yyyy");
        var stack = Stack
            .WithStyle(style => style.WithRowGap("24px"))
            .WithOrientation(Orientation.Vertical)
            .WithId(ActivityLogViewId)
            .WithView(
                DialogBodyStack
                    .WithView(icon)
                    .WithView(
                        Stack
                            .WithOrientation(Orientation.Vertical)
                            .WithView(
                                Stack
                                    .WithOrientation(Orientation.Horizontal)
                                    .WithView(Html("<em>by</em>"))
                                    .WithView(Html($"<strong>{log.User?.DisplayName}</strong>"))
                            )
                            .WithView(
                                Stack
                                    .WithOrientation(Orientation.Horizontal)
                                    .WithView(Html("<em>at</em>"))
                                    .WithView(Html($"<strong>{shortDateString}</strong>"))
                            )
                    )
            );

        if (log.Errors().Any())
        {
            var errorsStack = log.Errors()
                .Aggregate(
                    Stack.WithOrientation(Orientation.Vertical).WithView(Title("Errors", 3)),
                    (seed, message) => seed.WithView(Html($"<p>{message.Message}</p>"))
                );
            stack = stack.WithView(
                DialogBodyStack
                    .WithView(Icon(Icons.OpenSmc.XCircle, Colors.Failed) with { Size = "L" })
                    .WithView(errorsStack)
            );
        }
        if (log.Warnings().Any())
        {
            var warningsStack = log.Warnings()
                .Aggregate(
                    Stack.WithOrientation(Orientation.Vertical).WithView(Title("Warnings", 3)),
                    (seed, message) => seed.WithView(Html($"<p>{message.Message}</p>"))
                );
            stack = stack.WithView(
                DialogBodyStack
                    .WithView(Icon(Icons.OpenSmc.Alert, Colors.Warning) with { Size = "L" })
                    .WithView(warningsStack)
            );
        }
        if (log.Infos().Any())
        {
            var warningsStack = log.Infos()
                .Aggregate(
                    Stack.WithOrientation(Orientation.Vertical).WithView(Title("Infos", 3)),
                    (seed, message) => seed.WithView(Html($"<p>{message.Message}</p>"))
                );
            stack = stack.WithView(
                DialogBodyStack
                    .WithView(Icon(Icons.OpenSmc.Info, Colors.Info) with { Size = "L" })
                    .WithView(warningsStack)
            );
        }

        return stack;
    }

    public static LayoutStackControl DialogBodyStack => Stack.WithDialogBodyStyle();

    public static LayoutStackControl WithDialogBodyStyle(this LayoutStackControl stack) =>
        stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style.WithGap("24px").WithAlignItems("flex-start"));
}
