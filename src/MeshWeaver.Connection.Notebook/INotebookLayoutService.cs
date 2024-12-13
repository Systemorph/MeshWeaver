using MeshWeaver.Layout;

namespace MeshWeaver.Connection.Notebook;

public interface INotebookLayoutService
{
    UiControl GetArea(string area);
    void SetArea(string area, UiControl control);
    bool HasArea(string area);
}
