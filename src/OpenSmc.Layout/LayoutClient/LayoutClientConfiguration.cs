namespace OpenSmc.Layout.LayoutClient;

public record LayoutClientConfiguration(object LayoutHostAddress)
{
    internal object RefreshRequest { get; init; } = new AreaReference();
    internal string Area = string.Empty;
    public LayoutClientConfiguration WithRefreshRequest (object refreshRequest) =>
        this with { RefreshRequest = refreshRequest };

    internal string MainArea { get; init; } = "";

    public LayoutClientConfiguration WithMainArea(string mainArea) => 
        this with
        {
            MainArea = mainArea, 
            RefreshRequest = RefreshRequest is AreaReference refreshRequest ? refreshRequest with {Area = mainArea} : RefreshRequest
        };
}