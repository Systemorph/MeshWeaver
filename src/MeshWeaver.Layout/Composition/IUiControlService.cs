using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Formatting;

namespace MeshWeaver.Layout.Composition;

public interface IUiControlService
{
    UiControl? Convert(object o);
    void AddRule(Func<object, UiControl?> rule);

    LayoutDefinition LayoutDefinition { get; }
}

public class UiControlService(IMessageHub hub) : IUiControlService
{
    private ImmutableList<Func<object, UiControl?>> rules = [o => o as UiControl ?? DefaultConversion(o)];
    public UiControl? Convert(object o) => 
        rules.Select(r => r.Invoke(o))
            .FirstOrDefault(x => x is not null);

    public void AddRule(Func<object, UiControl?> rule)
    {
        rules = rules.Insert(0, rule);
    }

    public LayoutDefinition LayoutDefinition { get; } = hub.GetLayoutDefinition();



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
