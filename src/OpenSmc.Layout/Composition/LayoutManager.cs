using OpenSmc.Data;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Layout.Composition;

public static class LayoutManager
{

    public static ISynchronizationStream<EntityStore, LayoutAreaReference> Render(
        this LayoutAreaHost layoutArea,
        LayoutDefinition layoutDefinition
    )
    {
        var reference = layoutArea.Stream.Reference;
        var viewElement = layoutDefinition.GetViewElement(reference);
        if (viewElement == null)
            return layoutArea.Stream;

        layoutArea.RenderArea(new(reference.Area), viewElement);
        return layoutArea.Stream;
    }

    private static void RenderArea(this LayoutAreaHost layoutArea, RenderingContext context, object viewModel)
    {
        if (viewModel == null)
            return;

        var area = context.Area;
        if (viewModel is LayoutStackControl stack)
        {
            foreach (var ve in stack.ViewElements)
                layoutArea.RenderArea(context with{Area = $"{area}/{ve.Area}" }, ve);
            viewModel = stack with
            {
                Areas = stack.ViewElements.Select(ve => $"{area}/{ve.Area}").ToArray()
            };
        }



        if (viewModel is LayoutGridItemControl gridItem)
        {
            layoutArea.RenderArea(new($"{area}/{LayoutGridItemControl.ChildContentArea}"),  gridItem.ChildContent);
        }

        if (viewModel is SplitterPaneControl splitterPane)
        {
            layoutArea.RenderArea(new($"{area}/{SplitterPaneControl.ChildContentArea}"), splitterPane.ChildContent);
        }

        layoutArea.UpdateLayout(area, viewModel);
    }

    private static void RenderArea(this LayoutAreaHost layoutArea, RenderingContext context, ViewElementWithViewDefinition viewDefinition)
    {
        var area = context.Area;
        var stream = viewDefinition.ViewDefinition;
        layoutArea.UpdateLayout(area, new SpinnerControl());
        
        layoutArea.AddDisposable(area, stream.Subscribe(f =>
            layoutArea.InvokeAsync(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var control = await f.Invoke(layoutArea, context);
                layoutArea.RenderArea(context, control);
            })
        ));
    }

    private static void RenderArea(this LayoutAreaHost layoutArea, RenderingContext context, ViewElement viewElement)
    {
        switch (viewElement)
        {
            case ViewElementWithView view:
                layoutArea.RenderArea(context, view.View);
                break;
            case ViewElementWithViewDefinition viewDefinition:
                layoutArea.RenderArea(context, viewDefinition);
                break;
            case ViewElementWithViewStream s:
                layoutArea.Stream.AddDisposable(s.Stream.Invoke(layoutArea, context).Subscribe(c => layoutArea.RenderArea(context, c)));
                break;
            default:
                throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}");
        }
    }

}
