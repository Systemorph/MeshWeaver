using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Layout.Client;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public static class BlazorClientExtensions
{


    public static MessageHubConfiguration AddBlazor(this MessageHubConfiguration config) =>
        config.AddBlazor(x => x);

    public static MessageHubConfiguration AddBlazor(this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration> configuration) =>
        config
            .AddLayoutClient(c => configuration.Invoke(c.WithView(DefaultFormatting)));


    private static ViewDescriptor StandardView<TViewModel, TView>(TViewModel instance,
        IChangeStream<JsonElement, LayoutAreaReference> stream, string area) =>
        new(typeof(TView), new Dictionary<string, object> { { ViewModel, instance }, { nameof(Stream), stream }, { nameof(Area), area } });
    private static ViewDescriptor StandardView(Type tView, object instance,
        IChangeStream<JsonElement, LayoutAreaReference> stream, string area) =>
        new(tView, new Dictionary<string, object> { { ViewModel, instance }, { nameof(Stream), stream }, { nameof(Area), area } });

    public const string ViewModel = nameof(ViewModel);




    #region Standard Formatting
    private static ViewDescriptor DefaultFormatting(object instance,
        IChangeStream<JsonElement, LayoutAreaReference> stream, string area)
    {
        return instance switch
        {
            HtmlControl html =>
                StandardView<HtmlControl, HtmlView>(html, stream, area),
            LayoutStackControl stack =>
                StandardView<LayoutStackControl, LayoutStack>(stack, stream, area),
            MenuItemControl menu => StandardView<MenuItemControl, MenuItem>(menu, stream, area),
            LayoutAreaControl layoutArea => StandardView<LayoutAreaControl, LayoutAreaView>(layoutArea, stream, area),
            IGenericType gc => GenericView(gc, stream, area),
            _ => DelegateToDotnetInteractive(instance, stream, area)


        };
    }

    private static ViewDescriptor GenericView(IGenericType gc, IChangeStream<JsonElement, LayoutAreaReference> stream, string area)
    {
        var viewType = GenericTypeMaps.GetValueOrDefault(gc.BaseType)?.Invoke(gc);
        return StandardView(viewType, gc, stream, area);
    }

    private static readonly Dictionary<Type, Func<IGenericType, Type>> GenericTypeMaps = new()
    {
        { typeof(DataGridControl<>), t => typeof(DataGrid<>).MakeGenericType(t.TypeArguments) }
    };


    private static ViewDescriptor DelegateToDotnetInteractive(object instance,
        IChangeStream<JsonElement, LayoutAreaReference> stream, string area)
    {
        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        var output = Controls.Html(instance.ToDisplayString(mimeType));
        return new ViewDescriptor(typeof(HtmlView), ImmutableDictionary<string, object>.Empty.Add(ViewModel, output).Add(nameof(Stream), stream).Add(nameof(Area), area));
    }
#endregion
}
