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

        layoutArea.RenderArea(reference.Area, viewElement);
        return layoutArea.Stream;
    }

    private static void RenderArea(this LayoutAreaHost layoutArea, string area, object viewModel)
    {
        if (viewModel == null)
            return;

        if (viewModel is LayoutStackControl stack)
        {
            foreach (var ve in stack.ViewElements)
                layoutArea.RenderArea($"{area}/{ve.Area}", ve);
            viewModel = stack with
            {
                Areas = stack.ViewElements.Select(ve => $"{area}/{ve.Area}").ToArray()
            };
        }

        //if (viewModel is UiControl { DataContext: not null } control)
        //    viewModel = control with { DataContext = layoutArea.UpdateData(control.DataContext) };

        layoutArea.UpdateLayout(area, viewModel);
    }

    private static void RenderArea(this LayoutAreaHost layoutArea, string area, ViewElementWithViewDefinition viewDefinition)
    {
        var stream = viewDefinition.ViewDefinition;
        layoutArea.UpdateLayout(area, new SpinnerControl());
        
        layoutArea.AddDisposable(area, stream.Subscribe(f =>
            layoutArea.InvokeAsync(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var control = await f.Invoke(layoutArea);
                layoutArea.RenderArea(area, control);
            })
        ));
    }

    private static void RenderArea(this LayoutAreaHost layoutArea, string area, ViewElement viewElement)
    {
        switch (viewElement)
        {
            case ViewElementWithView view:
                layoutArea.RenderArea(area, view.View);
                break;
            case ViewElementWithViewDefinition viewDefinition:
                layoutArea.RenderArea(area, viewDefinition);
                break;
            case ViewElementWithViewStream s:
                layoutArea.Stream.AddDisposable(s.Stream.Invoke(layoutArea).Subscribe(c => layoutArea.RenderArea(area, c)));
                break;
            default:
                throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}");
        }
    }

}
