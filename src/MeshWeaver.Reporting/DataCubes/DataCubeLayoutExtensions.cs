using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using static MeshWeaver.Layout.Template;
using static MeshWeaver.Layout.Controls;

namespace MeshWeaver.Reporting.DataCubes;

public record FilterItem(object Id, string Label, bool Selected);

public record DataCubeFilter(IReadOnlyCollection<Dimension> AvailableDimensions)
{
    public string SelectedDimension { get; init; } = AvailableDimensions.FirstOrDefault()?.SystemName;
    public ImmutableDictionary<string, ImmutableList<FilterItem>> FilterItems { get; init; } 
        = ImmutableDictionary<string, ImmutableList<FilterItem>>.Empty;
    public string Search { get; init; }
    public virtual bool Equals(DataCubeFilter other)
    {
        if (other == null)
            return false;
        return Equals(SelectedDimension, other.SelectedDimension)
            && Equals(Search, other.Search)
            && FilterItems.Count == other.FilterItems.Count
            && FilterItems.All(i =>
                other.FilterItems.TryGetValue(i.Key, out var otherItem) 
                && Equals(i.Value, otherItem));
    }

    public override int GetHashCode()
    {
        unchecked // Overflow is fine, just wrap
        {
            var hash = 17;
            // Hash code for reference types (strings) can be obtained directly
            hash = hash * 23 + (SelectedDimension?.GetHashCode() ?? 0);
            hash = hash * 23 + (Search?.GetHashCode() ?? 0);

            // For the dictionary, combine the hash codes of keys and values
            foreach (var item in FilterItems)
            {
                hash = hash * 23 + item.Key.GetHashCode(); // Hash code for the key (string)
                hash = hash * 23 + item.Value.Aggregate(0, (current, val) => current + val.GetHashCode()); // Aggregate hash codes of values (FilterItem list)
            }

            return hash;
        }
    }
}

public static class DataCubeLayoutExtensions
{
    /*
     * 1. context panel not rendered until the button is clicked
     * 2. user clicks filter button
     * 3. list of dimensions is rendered from dataCube slices (put an observable of data-cube to layoutAreaHost variables)
     * 4. preselect first dimension
     * 5. call getSlices on selected dimensions, put it to data under current dimension name
     * 6. data-bind list of checkboxes
     *
     * building filtered data cube
     * 1. get unfiltered data cube (observable)
     * 2. for each dimension try get observable from data
     *
     */

    public static UiControl Render(this LayoutAreaHost host, DataCubeFilter filter, string filterId)
    {
        

        return Template.Bind<DataCubeFilter, UiControl>(filter, f =>
            Stack
                .WithView(Header("Filter"))
                .WithView(
                    Stack
                        .WithClass("dimension-filter")
                        .WithOrientation(Orientation.Horizontal)
                        .WithHorizontalGap(16)
                        .WithView(Listbox(f.SelectedDimension, f.AvailableDimensions
                                .Select(d => (Option)new Option<string>(d.SystemName, d.DisplayName))
                                .ToArray()))
                        .WithView(Stack
                            .WithClass("dimension-values")
                            .WithView(
                                Text(f.Search)
                                    .WithPlaceholder("Search...")
                                // TODO V10: this throws an "access violation" exception, figure out why (08.07.2024, Alexander Kravets)
                                // .WithImmediate(true)
                                // .WithImmediateDelay(200)
                            )
                            .WithView((a, c) => ToDimensionValues(a, filterId))
                            .WithVerticalGap(16)
                        )
                ), filterId);
    }

    public static DataCubeFilter CreateFilter(this IDataCube cube) => new(cube.GetAvailableDimensions());

    public static DataCubeFilter Update(this DataCubeFilter filter, IDataCube cube)
        => filter with
        {
            FilterItems = filter.FilterItems.SetItem(filter.SelectedDimension,
                filter.FilterItems.GetValueOrDefault(filter.SelectedDimension) ?? cube
                    .GetSlices(filter.SelectedDimension)
                    .SelectMany(s => s.Tuple.Select(
                        // todo get dimension pairs of type and ids from dataSlices
                        // go to the workspace and take observable of this type
                        x => new FilterItem(x.Value, x.Value.ToString(), true))
                    )
                    .DistinctBy(x =>
                        x.Id) // TODO V10: this is to be reviewed to find better place to reduce duplication of codes (2024/07/16, Dmitry Kalabin)
                    .ToImmutableList())
        };
    private static ImmutableList<Dimension> GetAvailableDimensions(this IDataCube dataCube)
    {
        return dataCube.GetDimensionDescriptors()
            .Select(d => new Dimension {SystemName = d.SystemName, DisplayName = d.Type.Name})
            .ToImmutableList();
    }

    private static IObservable<ItemTemplateControl> ToDimensionValues(LayoutAreaHost area, string filterId)
    {
        return area.GetDataStream<DataCubeFilter>(filterId)
            .Select(f => f.SelectedDimension)
            .DistinctUntilChanged()
        .Select(selectedDimension => 
                BindEnumerable<FilterItem, CheckBoxControl>(
                    new(LayoutAreaReference.GetDataPointer(filterId, "filterItems", selectedDimension)),
                f => CheckBox(f.Selected)
            ));
    }

    public const string DataCubeFilterId = "$DataCubeFilter";

    private static void OpenContextPanel(this LayoutAreaHost host, IObservable<IDataCube> cubeStream, IObservable<DataCubeFilter> filterStream)
    {

        var viewDefinition = cubeStream
            .CombineLatest(host.Stream.GetDataStream<DataCubeFilter>(DataCubeFilterId)
                    .DefaultIfEmpty(),
                (cube, filter) => (filter ?? cube.CreateFilter()).Update(cube))
            .Subscribe(filter =>
                host.UpdateArea(
                    new(StandardPageLayout.ContextMenu),
                    host.Render(filter, DataCubeFilterId)
                ));
    }



}

