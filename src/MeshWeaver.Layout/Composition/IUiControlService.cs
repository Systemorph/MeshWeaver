using System.Collections.Immutable;
using Microsoft.DotNet.Interactive.Formatting;

namespace MeshWeaver.Layout.Composition;

public interface IUiControlService
{
    UiControl Convert(object o);
    void RegisterRule(Func<object, UiControl> rule);

}

public class UiControlService : IUiControlService
{
    private ImmutableList<Func<object, UiControl>> rules = [o => o as UiControl ?? DefaultConversion(o)];
    public UiControl Convert(object o)
    {
        return rules.Select(r => r.Invoke(o)).First(x => x != null);
    }

    public void RegisterRule(Func<object, UiControl> rule)
    {
        rules = rules.Insert(0, rule);
    }

    private static UiControl DefaultConversion(object instance)
    {
        if (instance is UiControl control)
            return control;

        if (instance is IRenderableObject ro)
            return ro.ToControl();

        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        return Controls.Html(instance.ToDisplayString(mimeType));
    }
}
