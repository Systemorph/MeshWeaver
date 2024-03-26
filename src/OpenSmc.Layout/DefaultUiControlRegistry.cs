using Microsoft.DotNet.Interactive.Formatting;
using Formatter = Microsoft.DotNet.Interactive.Formatting.Formatter;

namespace OpenSmc.Layout;

public static class DefaultUiControlRegistry
{
    public static void RegisterDefaults(UiControlsManager service)
    {
        // TODO V10: complete stack trace (2022-10-10, Andrei Sirotenko)

        service.RegisterFallback(o =>
                                 {
                                     var mimeType = Formatter.GetPreferredMimeTypesFor(o?.GetType()).FirstOrDefault();
                                     return Controls.HtmlView(o.ToDisplayString(mimeType));
                                 });

        service.Register(typeof(Exception), o => Controls.Exception((Exception)o));


        service.Register(typeof(Nullable<>), instance =>
                                             {
                                                 // HACK V10: in case of nullable type has null value, we still have to show proper control, not html control (2023-08-31, Andrei Sirotenko)
                                                 return instance == null
                                                            ? service.Get(null)
                                                            : service.Get(instance, Nullable.GetUnderlyingType(instance.GetType()));
                                             });
    }
}