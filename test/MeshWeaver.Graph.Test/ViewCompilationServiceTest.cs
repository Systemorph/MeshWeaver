using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for ViewCompilationService - compiling C# view functions at runtime using Roslyn.
/// </summary>
public class ViewCompilationServiceTest
{
    private readonly ViewCompilationService _service;

    public ViewCompilationServiceTest()
    {
        _service = new ViewCompilationService();
    }

    [Fact(Timeout = 15000)]
    public async Task CompileViewAsync_CompilesSimpleView()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "simple-view",
            Area = "SimpleView",
            ViewSource = @"
public static UiControl Render(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Html(""<h1>Hello World</h1>"");
}"
        };

        // Act
        var compiledView = await _service.CompileViewAsync(config);

        // Assert
        compiledView.Should().NotBeNull();
    }

    [Fact(Timeout = 15000)]
    public async Task CompileViewAsync_CompilesViewWithControlsUsage()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "controls-view",
            Area = "ControlsView",
            ViewSource = @"
public static UiControl Render(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Stack
        .WithView(Controls.Html(""<h1>Title</h1>""))
        .WithView(Controls.Button(""Click Me""));
}"
        };

        // Act
        var compiledView = await _service.CompileViewAsync(config);

        // Assert
        compiledView.Should().NotBeNull();
    }

    [Fact(Timeout = 15000)]
    public async Task CompileViewAsync_SetsCompiledViewOnConfig()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "set-compiled",
            Area = "SetCompiled",
            ViewSource = @"
public static UiControl View(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Html(""<div>Test</div>"");
}"
        };

        // Act
        await _service.CompileViewAsync(config);

        // Assert
        config.CompiledView.Should().NotBeNull();
    }

    [Fact(Timeout = 15000)]
    public async Task CompileViewAsync_CachesCompiledView()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "cached-view",
            Area = "CachedView",
            ViewSource = @"
public static UiControl MyView(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Html(""<span>Cached</span>"");
}"
        };

        // Act
        var firstCompile = await _service.CompileViewAsync(config);
        var secondCompile = await _service.CompileViewAsync(config);

        // Assert
        firstCompile.Should().BeSameAs(secondCompile, "Same delegate should be returned from cache");
    }

    [Fact(Timeout = 5000)]
    public void GetCompiledView_ReturnsNullForUnknownId()
    {
        // Act
        var result = _service.GetCompiledView("unknown-id");

        // Assert
        result.Should().BeNull();
    }

    [Fact(Timeout = 15000)]
    public async Task GetCompiledView_ReturnsViewAfterCompilation()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "get-view-test",
            Area = "GetViewTest",
            ViewSource = @"
public static UiControl ViewMethod(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Html(""<div>Found</div>"");
}"
        };

        await _service.CompileViewAsync(config);

        // Act
        var result = _service.GetCompiledView("get-view-test");

        // Assert
        result.Should().NotBeNull();
    }

    [Fact(Timeout = 30000)]
    public async Task CompileAllAsync_CompilesMultipleViews()
    {
        // Arrange
        var configs = new[]
        {
            new LayoutAreaConfig
            {
                Id = "view1",
                Area = "View1",
                ViewSource = "public static UiControl View1(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(\"<p>1</p>\");"
            },
            new LayoutAreaConfig
            {
                Id = "view2",
                Area = "View2",
                ViewSource = "public static UiControl View2(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(\"<p>2</p>\");"
            },
            new LayoutAreaConfig
            {
                Id = "view3",
                Area = "View3",
                ViewSource = "public static UiControl View3(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(\"<p>3</p>\");"
            }
        };

        // Act
        var result = await _service.CompileAllAsync(configs);

        // Assert
        result.Should().HaveCount(3);
        result.Should().ContainKey("view1");
        result.Should().ContainKey("view2");
        result.Should().ContainKey("view3");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileAllAsync_SkipsConfigsWithoutViewSource()
    {
        // Arrange
        var configs = new[]
        {
            new LayoutAreaConfig
            {
                Id = "with-source",
                Area = "WithSource",
                ViewSource = "public static UiControl View(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(\"<p>ok</p>\");"
            },
            new LayoutAreaConfig
            {
                Id = "without-source",
                Area = "WithoutSource",
                ViewSource = null
            }
        };

        // Act
        var result = await _service.CompileAllAsync(configs);

        // Assert
        result.Should().HaveCount(1);
        result.Should().ContainKey("with-source");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileViewAsync_ThrowsForInvalidCode()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "invalid-view",
            Area = "InvalidView",
            ViewSource = @"
public static UiControl InvalidView(LayoutAreaHost host, RenderingContext ctx)
{
    return // syntax error - missing expression
}"
        };

        // Act & Assert
        var act = () => _service.CompileViewAsync(config);
        await act.Should().ThrowAsync<ViewCompilationException>();
    }

    [Fact(Timeout = 15000)]
    public async Task CompileViewAsync_ThrowsForMissingViewMethod()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "no-view-method",
            Area = "NoViewMethod",
            ViewSource = @"
public static string NotAView()
{
    return ""not a view"";
}"
        };

        // Act & Assert
        var act = () => _service.CompileViewAsync(config);
        await act.Should().ThrowAsync<ViewCompilationException>()
            .WithMessage("*No view method found*");
    }

    [Fact(Timeout = 15000)]
    public async Task CompileViewAsync_ThrowsForMultipleViewMethods()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "multiple-views",
            Area = "MultipleViews",
            ViewSource = @"
public static UiControl View1(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(""<p>1</p>"");
public static UiControl View2(LayoutAreaHost host, RenderingContext ctx) => Controls.Html(""<p>2</p>"");"
        };

        // Act & Assert
        var act = () => _service.CompileViewAsync(config);
        await act.Should().ThrowAsync<ViewCompilationException>()
            .WithMessage("*Multiple view methods found*");
    }

    [Fact(Timeout = 10000)]
    public async Task CompileViewAsync_ThrowsForNullViewSource()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "null-source",
            Area = "NullSource",
            ViewSource = null
        };

        // Act & Assert
        var act = () => _service.CompileViewAsync(config);
        await act.Should().ThrowAsync<ViewCompilationException>()
            .WithMessage("*cannot be null or empty*");
    }

    [Fact(Timeout = 10000)]
    public async Task CompileViewAsync_ThrowsForEmptyViewSource()
    {
        // Arrange
        var config = new LayoutAreaConfig
        {
            Id = "empty-source",
            Area = "EmptySource",
            ViewSource = "   "
        };

        // Act & Assert
        var act = () => _service.CompileViewAsync(config);
        await act.Should().ThrowAsync<ViewCompilationException>()
            .WithMessage("*cannot be null or empty*");
    }
}
