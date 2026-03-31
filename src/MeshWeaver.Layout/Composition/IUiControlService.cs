using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Formatting;

namespace MeshWeaver.Layout.Composition;

public interface IUiControlService
{
    UiControl? Convert(object o);

    LayoutDefinition LayoutDefinition { get; }
}

public class UiControlService(IMessageHub hub) : IUiControlService
{
    public LayoutDefinition LayoutDefinition { get; } = hub.GetLayoutDefinition();

    // Rules are immutable: config-time rules from LayoutDefinition + default fallback.
    // No AddRule() — all rules are known at construction time.
    private ImmutableList<Func<object, UiControl?>> Rules { get; } = BuildRules(hub.GetLayoutDefinition());

    private static ImmutableList<Func<object, UiControl?>> BuildRules(LayoutDefinition layout)
        => layout.ConversionRules.Add(o => o as UiControl ?? DefaultConversion(o));

    public UiControl? Convert(object o) =>
        Rules.Select(r => r.Invoke(o))
            .FirstOrDefault(x => x is not null);



    private static UiControl? DefaultConversion(object instance)
    {
        if (instance is UiControl control)
            return control;

        if (instance is IRenderableObject ro)
            return ro.ToControl();

        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        return Controls.Html(instance!.ToDisplayString(mimeType));
    }
}
