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

        layoutArea.RenderArea(new(reference.Area){IsTopLevel = true}, viewElement);
        return layoutArea.Stream;
    }

    public static void RenderArea(this LayoutAreaHost layoutArea, RenderingContext context, object viewModel)
    {
        if (viewModel == null)
            return;

        var area = context.Area;

        if (viewModel is UiControl control)
        {
            var dataContext = control.DataContext ?? context.DataContext;
            
            viewModel = control with
            {
                DataContext = dataContext
            };

            if (viewModel is IContainerControl container)
            {
                foreach (var ve in container.ChildControls)
                {
                    layoutArea.RenderArea(context with { Area = $"{area}/{ve.Area}", DataContext = dataContext, IsTopLevel = false}, ve);
                }

                viewModel = container.SetAreas(container.ChildControls.Select(ve => $"{area}/{ve.Area}").ToArray());
            }
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

    public static void RenderArea(this LayoutAreaHost layoutArea, RenderingContext context, ViewElement viewElement)
    {
        layoutArea.DisposeExistingAreas(context);

        switch (viewElement)
        {
            case ViewElementWithView view:
                layoutArea.RenderArea(context, view.View);
                break;
            case ViewElementWithViewDefinition viewDefinition:
                layoutArea.RenderArea(context, viewDefinition);
                break;
            case ViewElementWithViewStream s:
                layoutArea.AddDisposable(context.Area, s.Stream.Invoke(layoutArea, context)
                    .Subscribe(c => layoutArea.RenderArea(context, c)));
                break;
            default:
                throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}");
        }
    }

}
