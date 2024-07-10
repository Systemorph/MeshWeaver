using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.DataCubes;
using OpenSmc.Domain;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Northwind.Domain;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind.ViewModel;

public record DataCubeFilter
{
    public string SelectedDimension { get; init; }
    public ImmutableDictionary<string, ImmutableList<FilterItem>> FilterItems { get; init; } = ImmutableDictionary<string, ImmutableList<FilterItem>>.Empty;
}

public static class DataCubeLayoutExtensions
{
    public const string FilterId = "Filter";

    public static UiControl ToDataCubeFilter(this IDataCube dataCube, LayoutAreaHost area, RenderingContext context)
    {
        var filter = area.Stream.GetData<DataCubeFilter>(FilterId) ?? new();
        var availableDimensions = dataCube.GetAvailableDimensions();
        filter = filter with { SelectedDimension = filter.SelectedDimension ?? availableDimensions.First().SystemName };
        
        // TODO V10: add overload that accepts lambda (09.07.2024, Alexander Kravets)
        // area.UpdateData(FilterId, filter);

        area.AddDisposable(context.Area, 
            area.GetDataStream<DataCubeFilter>(FilterId)
                .DistinctUntilChanged()
                .Subscribe(f =>
                {
                    if (!f.FilterItems.ContainsKey(f.SelectedDimension))
                    {
                        f = f with
                        {
                            FilterItems = f.FilterItems.Add(
                                f.SelectedDimension,
                                dataCube.GetFilterItems(f.SelectedDimension)
                            )
                        };
                    }
        
                    area.UpdateData(FilterId, f);
                })
            );

        return area.Bind(filter, FilterId, f =>
            Stack()
                .WithView(Header("Filter"))
                .WithView(
                    Stack()
                        .WithClass("dimension-filter")
                        .WithOrientation(Orientation.Horizontal)
                        .WithHorizontalGap(16)
                        .WithView(
                            Listbox(f.SelectedDimension)
                                .WithOptions(
                                    availableDimensions
                                        .Select(d => new Option<string>(d.SystemName, d.DisplayName))
                                        .ToArray()
                                )
                        )
                        .WithView(Stack()
                            .WithClass("dimension-values")
                            .WithView(
                                TextBox()
                                    .WithSkin(TextBoxSkin.Search)
                                    .WithPlaceholder("Search...")
                                // TODO V10: this throws an "access violation" exception, figure out why (08.07.2024, Alexander Kravets)
                                // .WithImmediate(true)
                                // .WithImmediateDelay(200)
                            )
                            .WithView(dataCube.ToDimensionValues)
                            .WithVerticalGap(16)
                        )
                    )
        );
    }

    private static ImmutableList<Dimension> GetAvailableDimensions(this IDataCube dataCube)
    {
        return dataCube.GetDimensionDescriptors()
            .Select(d => new Dimension {SystemName = d.SystemName, DisplayName = d.Type.Name})
            .ToImmutableList();
    }

    private static IObservable<ItemTemplateControl> ToDimensionValues(this IDataCube dataCube, LayoutAreaHost area, RenderingContext context)
    {
        return area.GetDataStream<DataCubeFilter>(FilterId)
            .DistinctUntilChanged()
            .Select(filter => area.BindEnumerable<FilterItem, CheckBoxControl>(
                LayoutAreaReference.GetDataPointer(FilterId + $"/{filter.SelectedDimension}"),
                f => CheckBox(f.Label, f.Selected)
            ));
    }

    // TODO V10: get data cube, get slices of selected dimension (09.07.2024, Alexander Kravets)
    private static ImmutableList<FilterItem> GetFilterItems(this IDataCube dataCube, string selectedDimension)
    {
        return ImmutableList<FilterItem>.Empty;
        // return dataCube.GetSlices(selectedDimension)
        //     .SelectMany(s => 
        //         s.Tuple.Select(x => new FilterItem(x.Dimension, x.Value))
        //         )
    }

    // list of dimensions => from dataCube.GetDimensionDescriptors
    // area.Workspace => dataContext
    private static IReadOnlyDictionary<string, Type> FilterDimensions =>
        new[] { typeof(Customer), typeof(Product), typeof(Supplier) }.ToDictionary(t => t.FullName);

    // private static object Filter(LayoutAreaHost area, RenderingContext context)
    // {
    //     /*
    //      * 1. context panel not rendered until the button is clicked
    //      * 2. user clicks filter button
    //      * 3. list of dimensions is rendered from dataCube slices (put an observable of data-cube to layoutAreaHost variables)
    //      * 4. preselect first dimension
    //      * 5. call getSlices on selected dimensions, put it to data under current dimension name
    //      * 6. data-bind list of checkboxes
    //      *
    //      * building filtered data cube
    //      * 1. get unfiltered data cube (observable)
    //      * 2. for each dimension try get observable from data
    //      *
    //      */
    //     return area.Bind(
    //         new DataCubeFilter(FilterDimensions.First().Key),
    //         nameof(Filter),
    //         filter =>
    //             Stack()
    //                 .WithView(Header("Filter"))
    //                 .WithView(
    //                     Stack()
    //                         .WithClass("dimension-filter")
    //                         .WithOrientation(Orientation.Horizontal)
    //                         .WithHorizontalGap(16)
    //                         .WithView(Listbox(filter.Dimension)
    //                             .WithOptions(
    //                                 FilterDimensions
    //                                     .Select(d => new Option<string>(d.Value.FullName, d.Value.Name))
    //                                     .ToArray()
    //                             )
    //                         )
    //                         .WithView(Stack()
    //                             .WithClass("dimension-values")
    //                             .WithView(
    //                                 TextBox(filter.Search)
    //                                     .WithSkin(TextBoxSkin.Search)
    //                                     .WithPlaceholder("Search...")
    //                                 // TODO V10: this throws an "access violation" exception, figure out why (08.07.2024, Alexander Kravets)
    //                                 // .WithImmediate(true)
    //                                 // .WithImmediateDelay(200)
    //                                 )
    //                             // .WithView(DimensionValues)
    //                             .WithVerticalGap(16)
    //                         )
    //                 )
    //     );
    // }
}

