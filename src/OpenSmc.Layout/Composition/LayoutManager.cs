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

        var options = viewElement.Properties;
        if(reference.RenderLayout && layoutDefinition.MainLayout != null)
            viewElement = layoutDefinition.MainLayout(viewElement, layoutDefinition.NavMenu?.Invoke(reference));

        layoutArea.AddDocumentationOptions(options);

        layoutArea.RenderArea(new(reference.Area, options), viewElement);
        return layoutArea.Stream;
    }
    private static void AddDocumentationOptions(this LayoutAreaHost area, LayoutAreaProperties options)
    {
        area.UpdateProperties(LayoutAreaProperties.Properties, new LayoutAreaProperties{HeadingMenu = options.HeadingMenu});
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
                DataContext = dataContext,
            };

            if (viewModel is IContainerControl container)
            {
                foreach (var ve in container.SubAreas)
                {
                    layoutArea.RenderArea(context with { Area = $"{area}/{ve.Area}", DataContext = dataContext}, ve);
                }

                viewModel = container.SetAreas(container.SubAreas.Select(ve => $"{area}/{ve.Area}").ToArray());
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
                var control = await f.Invoke(layoutArea, context, ct);
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
