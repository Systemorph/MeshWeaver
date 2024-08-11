using System.Text.Json;
using Microsoft.FluentUI.AspNetCore.Components;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using JustifyContent=Microsoft.FluentUI.AspNetCore.Components.JustifyContent;
using Orientation=Microsoft.FluentUI.AspNetCore.Components.Orientation;
using SelectPosition=Microsoft.FluentUI.AspNetCore.Components.SelectPosition;
using HorizontalAlignment=Microsoft.FluentUI.AspNetCore.Components.HorizontalAlignment;
using Typography=Microsoft.FluentUI.AspNetCore.Components.Typography;
using FontWeight=Microsoft.FluentUI.AspNetCore.Components.FontWeight;
using MeshWeaver.Data;
using System.Reactive.Linq;
using Json.Pointer;
namespace MeshWeaver.Blazor;

public static class ViewModelExtensions
{
    public static JustifyContent ToFluentJustifyContent(this Layout.JustifyContent justify) =>
        justify switch
        {
            Layout.JustifyContent.FlexStart => JustifyContent.FlexStart,
            Layout.JustifyContent.Center => JustifyContent.Center,
            Layout.JustifyContent.FlexEnd => JustifyContent.FlexEnd,
            Layout.JustifyContent.SpaceBetween => JustifyContent.SpaceBetween,
            Layout.JustifyContent.SpaceAround => JustifyContent.SpaceAround,
            Layout.JustifyContent.SpaceEvenly => JustifyContent.SpaceEvenly,
            _ => JustifyContent.FlexStart
        };

    public static Microsoft.FluentUI.AspNetCore.Components.Icon ToFluentIcon(this Application.Styles.Icon icon) =>
        icon.Provider switch
        {
            FluentIcons.Provider =>
                Icons.GetInstance(new IconInfo { Name = icon.Id, Size = IconSize.Size24 }),
            _ => null
        };

    public static Orientation ToFluentOrientation(this Layout.Orientation orientation) => 
        orientation switch
        {
            Layout.Orientation.Horizontal => Orientation.Horizontal,
            Layout.Orientation.Vertical => Orientation.Vertical,
            _ => Orientation.Horizontal
        };

    public static SelectPosition ToFluentPosition(this Layout.SelectPosition position) =>
        position switch
        {
            Layout.SelectPosition.Above => SelectPosition.Above,
            Layout.SelectPosition.Below => SelectPosition.Below,
            _ => SelectPosition.Above
        };

    public static HorizontalAlignment ToFluentHorizontalAlignment(this Layout.HorizontalAlignment alignment) =>
        alignment switch
        {
            Layout.HorizontalAlignment.Left => HorizontalAlignment.Left,
            Layout.HorizontalAlignment.Center => HorizontalAlignment.Center,
            Layout.HorizontalAlignment.Right => HorizontalAlignment.Right,
            Layout.HorizontalAlignment.Start => HorizontalAlignment.Start,
            Layout.HorizontalAlignment.End => HorizontalAlignment.End,
            _ => HorizontalAlignment.Left
        };

    public static Typography ToFluentTypography(this Layout.Typography typography) =>
        typography switch
        {
            Layout.Typography.Body => Typography.Body,
            Layout.Typography.Subject => Typography.Subject,
            Layout.Typography.Header => Typography.Header,
            Layout.Typography.PaneHeader => Typography.PaneHeader,
            Layout.Typography.EmailHeader => Typography.EmailHeader,
            Layout.Typography.PageTitle => Typography.PageTitle,
            Layout.Typography.HeroTitle => Typography.HeroTitle,
            Layout.Typography.H1 => Typography.H1,
            Layout.Typography.H2 => Typography.H2,
            Layout.Typography.H3 => Typography.H3,
            Layout.Typography.H4 => Typography.H4,
            Layout.Typography.H5 => Typography.H5,
            Layout.Typography.H6 => Typography.H6,
            _ => Typography.Body
        };

    public static FontWeight ToFluentFontWeight(this Layout.FontWeight fontWeight) =>
        fontWeight switch
        {
            Layout.FontWeight.Normal => FontWeight.Normal,
            Layout.FontWeight.Bold => FontWeight.Bold,
            Layout.FontWeight.Bolder => FontWeight.Bolder,
            _ => FontWeight.Normal
        };

    public static  UiControl GetControl(this ISynchronizationStream<JsonElement> stream, ChangeItem<JsonElement> item, string area)
    {
        return item.Value.TryGetProperty(LayoutAreaReference.Areas, out var controls)
               && controls.TryGetProperty(JsonSerializer.Serialize(area), out var node)
            ? node.Deserialize<UiControl>(stream.Hub.JsonSerializerOptions)
            : null;
    }

    public static IObservable<T> GetObservable<T>(this ISynchronizationStream<JsonElement> stream, string dataContext, object value, Func<object,T> conversion) 
    {
        if (value is null)
            return Observable.Empty<T>();
        if (value is IObservable<T> observable)
            return observable;
        if (value is JsonPointerReference reference)
            return stream.Where(x => !stream.Hub.Address.Equals(x.ChangedBy))
                .Select(x => stream.Extract<T>(dataContext + reference.Pointer.TrimEnd('/'), x, reference));

        if(conversion != null)
            return Observable.Return(conversion.Invoke(value));
        if (value is T t)
            return Observable.Return(t);


        // TODO V10: Should we add more ways to convert? Converting to primitives? (11.06.2024, Roland Bürgi)
        throw new InvalidOperationException($"Cannot bind to {value.GetType().Name}");
    }

    private static TResult Extract<TResult>(this ISynchronizationStream<JsonElement> stream, string basePath, ChangeItem<JsonElement> changeItem, JsonPointerReference reference)
    {
        if (reference == null)
            return default;
        var pointer = JsonPointer.Parse(basePath);
        var ret = pointer.Evaluate(changeItem.Value);
        return ret == null ? default : ret.Value.Deserialize<TResult>(stream.Hub.JsonSerializerOptions);
    }

}
