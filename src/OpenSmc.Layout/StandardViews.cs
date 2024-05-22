using OpenSmc.Activities;
using OpenSmc.Application.Styles;
using OpenSmc.Layout.Views;
using OpenSmc.Utils;
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
        var stack = Stack()
            .WithStyle(style => style.WithRowGap("24px"))
            .WithSkin(Skin.VerticalPanel)
            .WithId(ActivityLogViewId)
            .WithView(
                DialogBodyStack()
                    .WithView(icon)
                    .WithView(
                        Stack()
                            .WithSkin(Skin.VerticalPanel)
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(HtmlView("<em>by</em>"))
                                    .WithView(HtmlView($"<strong>{log.User?.DisplayName}</strong>"))
                            )
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(HtmlView("<em>at</em>"))
                                    .WithView(HtmlView($"<strong>{shortDateString}</strong>"))
                            )
                    )
            );

        if (log.Errors().Any())
        {
            var errorsStack = log.Errors()
                .Aggregate(
                    Stack().WithSkin(Skin.VerticalPanel).WithView(Title("Errors", 3)),
                    (seed, message) => seed.WithView(HtmlView($"<p>{message.Message}</p>"))
                );
            stack = stack.WithView(
                DialogBodyStack()
                    .WithView(Icon(Icons.OpenSmc.XCircle, Colors.Failed) with { Size = "L" })
                    .WithView(errorsStack)
            );
        }
        if (log.Warnings().Any())
        {
            var warningsStack = log.Warnings()
                .Aggregate(
                    Stack().WithSkin(Skin.VerticalPanel).WithView(Title("Warnings", 3)),
                    (seed, message) => seed.WithView(HtmlView($"<p>{message.Message}</p>"))
                );
            stack = stack.WithView(
                DialogBodyStack()
                    .WithView(Icon(Icons.OpenSmc.Alert, Colors.Warning) with { Size = "L" })
                    .WithView(warningsStack)
            );
        }
        if (log.Infos().Any())
        {
            var warningsStack = log.Infos()
                .Aggregate(
                    Stack().WithSkin(Skin.VerticalPanel).WithView(Title("Infos", 3)),
                    (seed, message) => seed.WithView(HtmlView($"<p>{message.Message}</p>"))
                );
            stack = stack.WithView(
                DialogBodyStack()
                    .WithView(Icon(Icons.OpenSmc.Info, Colors.Info) with { Size = "L" })
                    .WithView(warningsStack)
            );
        }

        return stack;
    }

    public static LayoutStackControl RenderImportButton(
        string title,
        string description,
        string buttonArea,
        object address,
        string buttonId
    )
    {
        return Stack()
            .WithStyle(x => x.WithMinWidth(120))
            .WithView(Icon(Icons.OpenSmc.UploadCloud, Colors.Button))
            .WithView(HtmlView($"<strong>{title}</strong> <em>{description}</em>"))
            .WithView(
                Menu("Import")
                    .WithId(buttonId)
                    .WithColor(Colors.Button)
                    .WithClickMessage(new ClickedEvent(buttonArea), address)
            )
            .WithSkin(Skin.Action);
    }

    public static ActivityControl RenderHistoryLog(
        this ActivityLog log,
        Func<string, string> colorPickerFunction
    )
    {
        return new ActivityControl(
            log.User,
            log.Messages.FirstOrDefault()?.Message
                ?? //this is good for demo, if we want to make this complete, we need to change this logic
                log.SubActivities.FirstOrDefault()?.Messages.FirstOrDefault()?.Message
                ?? $"Has performed {log.Category.Wordify()}",
            null,
            Title(
                log.Messages.FirstOrDefault()?.Message ?? $"Has performed {log.Category.Wordify()}",
                5
            ),
            log.GetColorBasedOnStatus(colorPickerFunction),
            log.Start,
            null,
            null
        );
    }

    private static string GetColorBasedOnStatus(
        this ActivityLog log,
        Func<string, string> colorPickerFunction
    )
    {
        return log.Status == ActivityLogStatus.Failed
            ? Colors.Failed
            : colorPickerFunction(log.Category);
    }

    public static LayoutStackControl VerticalStackWithGap(
        this LayoutStackControl control,
        string gap = "16px"
    ) => control.WithSkin(Skin.VerticalPanel).WithStyle(style => style.WithGap(gap));

    public static LayoutStackControl DialogBodyStack() => Stack().WithDialogBodyStyle();

    public static LayoutStackControl WithDialogBodyStyle(this LayoutStackControl stack) =>
        stack
            .WithSkin("HorizontalPanel")
            .WithStyle(style => style.WithGap("24px").WithAlignItems("flex-start"));
}
