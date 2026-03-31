using System.Collections.Immutable;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;

namespace MeshWeaver.Blazor.Components;

public partial class CatalogView
{
    private ImmutableList<CatalogGroup> Groups { get; set; } = [];
    private bool CollapsibleSections { get; set; } = true;
    private bool ShowCounts { get; set; } = true;
    private int Xs { get; set; } = 12;
    private int Sm { get; set; } = 6;
    private int Md { get; set; } = 4;
    private int Lg { get; set; } = 3;
    private int Spacing { get; set; } = 2;
    private int SectionGap { get; set; } = 16;
    private int CardHeight { get; set; } = 140;

    private Dictionary<string, bool> expandedGroups = new();

    private string ContainerStyle => "display: flex; flex-direction: column;";
    private string CardStyle => $"height: {CardHeight}px; min-height: {CardHeight}px; max-height: {CardHeight}px; overflow: hidden; border: 1px solid var(--neutral-stroke-rest); border-radius: 4px; background: var(--neutral-layer-card-container);";

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Groups, x => x.Groups);
        DataBind(ViewModel.CollapsibleSections, x => x.CollapsibleSections);
        DataBind(ViewModel.ShowCounts, x => x.ShowCounts);
        DataBind(ViewModel.Xs, x => x.Xs);
        DataBind(ViewModel.Sm, x => x.Sm);
        DataBind(ViewModel.Md, x => x.Md);
        DataBind(ViewModel.Lg, x => x.Lg);
        DataBind(ViewModel.Spacing, x => x.Spacing);
        DataBind(ViewModel.SectionGap, x => x.SectionGap);
        DataBind(ViewModel.CardHeight, x => x.CardHeight);

        // Initialize expanded state from groups
        foreach (var group in ViewModel.Groups)
        {
            if (!expandedGroups.ContainsKey(group.Key))
            {
                expandedGroups[group.Key] = group.IsExpanded;
            }
        }
    }

    private void OnExpandedChanged(string groupKey, bool expanded)
    {
        expandedGroups[groupKey] = expanded;
        StateHasChanged();
    }
}
