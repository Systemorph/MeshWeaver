namespace MeshWeaver.Portal;


/// <summary>
/// Layout is the counterparty to UI. UiAddress(Id="1") corresponds to LayoutAddress(Id="1")
/// Ui Address will call RefreshRequest{Path={path constructed from Url}}
/// In packend we must have a plugin which reacts to this path.
/// </summary>
/// <param name="Id"></param>
public record LayoutAddress(string Id);