using MeshWeaver.Layout;

namespace MeshWeaver.Connection.Notebook;

public interface INotebookLayoutService
{
    UiControl GetControl(string area);
    void SetControl(string area, UiControl control);
    bool HasArea(string area);
}
