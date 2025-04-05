using MeshWeaver.Layout;
using ModuleSetup = MeshWeaver.Layout.ModuleSetup;

namespace MeshWeaver.Articles;

public record ArticleControl(object Article) : UiControl<ArticleControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
