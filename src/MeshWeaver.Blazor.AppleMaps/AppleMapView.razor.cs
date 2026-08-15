using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;
using MeshWeaver.Maps;
using MeshWeaver.Layout;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Blazor.AppleMaps;

/// <summary>
/// Blazor view that renders a <c>MapControl</c> with Apple MapKit JS. Needs an
/// <c>AppleMaps:Token</c> (a MapKit JS JWT); without one the view logs and renders the empty
/// container rather than attempting a script load that can only fail. Structurally the twin of
/// the other map providers, so a deployment swaps providers purely by which module its
/// <c>Modules:Assemblies</c> lists.
/// </summary>
public partial class AppleMapView : BlazorView<MapControl, AppleMapView>
{
    [Inject] private IOptions<AppleMapsConfiguration> Configuration { get; set; } = null!;

    private string Token => Configuration.Value.Token;
    private string MapId { get; set; } = null!;
    private IJSObjectReference? jsModule;

    /// <summary>
    /// Loads the JavaScript module and initializes the map on first render, then keeps markers
    /// and circles in sync on subsequent renders.
    /// </summary>
    /// <param name="firstRender"><c>true</c> on the component's first render; otherwise <c>false</c>.</param>
    /// <returns>A task that completes when post-render work is finished.</returns>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            if (string.IsNullOrEmpty(Token))
            {
                Logger.LogWarning("AppleMaps:Token is not configured — the Apple MapKit view renders empty. " +
                                  "Mint a MapKit JS token from an Apple Developer MapKit key and set AppleMaps:Token.");
                return;
            }
            try
            {
                jsModule = await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/MeshWeaver.Blazor.AppleMaps/AppleMapView.razor.js");
                await InitializeMap();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error initializing Apple MapKit view");
            }
        }
        else
        {
            try
            {
                await UpdateMarkers();
                await UpdateCircles();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating Apple MapKit overlays");
            }
        }
        await base.OnAfterRenderAsync(firstRender);
    }

    private async Task InitializeMap()
    {
        if (jsModule == null)
            return;
        try
        {
            var controlOptions = ViewModel.Options;
            var mapOptions = new
            {
                zoom = controlOptions?.Zoom ?? 10,
                center = controlOptions?.Center != null
                    ? new { lat = controlOptions.Center.Lat, lng = controlOptions.Center.Lng }
                    : new { lat = 0.0, lng = 0.0 },
                mapTypeId = controlOptions?.MapTypeId ?? "roadmap",
                disableDefaultUI = controlOptions?.DisableDefaultUI ?? false,
                zoomControl = controlOptions?.ZoomControl ?? true,
                mapTypeControl = controlOptions?.MapTypeControl ?? true
            };
            await jsModule.InvokeVoidAsync("initializeMap", MapId, mapOptions, Token);
            await jsModule.InvokeVoidAsync("setMarkerClickCallback", MapId, DotNetObjectReference.Create(this));
            await jsModule.InvokeVoidAsync("setCircleClickCallback", MapId, DotNetObjectReference.Create(this));
            await UpdateMarkers();
            await UpdateCircles();
            StateHasChanged();
        }
        catch (JSDisconnectedException)
        {
            Logger.LogDebug("JavaScript runtime disconnected during map initialization");
        }
        catch (ObjectDisposedException)
        {
            Logger.LogDebug("JavaScript module disposed during map initialization");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize Apple MapKit map");
        }
    }

    private async Task UpdateMarkers()
    {
        if (string.IsNullOrEmpty(MapId) || ViewModel.Markers == null || jsModule == null || !ViewModel.Markers.Any())
            return;
        try
        {
            var markerConfigs = ViewModel.Markers.Select(m => new
            {
                id = m.Id ?? Guid.NewGuid().ToString(),
                position = new { lat = m.Position.Lat, lng = m.Position.Lng },
                title = m.Title ?? "",
                label = m.Label ?? "",
                draggable = m.Draggable,
                icon = m.Icon
            }).ToArray();
            await jsModule.InvokeVoidAsync("updateMarkers", MapId, markerConfigs);
        }
        catch (JSDisconnectedException)
        {
            Logger.LogDebug("JavaScript runtime disconnected, skipping marker update for map {MapId}", MapId);
        }
        catch (ObjectDisposedException)
        {
            Logger.LogDebug("JavaScript module disposed, skipping marker update for map {MapId}", MapId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update markers for map {MapId}", MapId);
        }
    }

    private async Task UpdateCircles()
    {
        if (string.IsNullOrEmpty(MapId) || ViewModel.Circles == null || jsModule == null || !ViewModel.Circles.Any())
            return;
        try
        {
            var circleConfigs = ViewModel.Circles.Select(c => new
            {
                id = c.Id ?? Guid.NewGuid().ToString(),
                center = new { lat = c.Center.Lat, lng = c.Center.Lng },
                radius = c.Radius,
                fillColor = c.FillColor ?? "#FF0000",
                fillOpacity = c.FillOpacity,
                strokeColor = c.StrokeColor ?? "#FF0000",
                strokeOpacity = c.StrokeOpacity,
                strokeWeight = c.StrokeWeight
            }).ToArray();
            await jsModule.InvokeVoidAsync("updateCircles", MapId, circleConfigs);
        }
        catch (JSDisconnectedException)
        {
            Logger.LogDebug("JavaScript runtime disconnected, skipping circle update for map {MapId}", MapId);
        }
        catch (ObjectDisposedException)
        {
            Logger.LogDebug("JavaScript module disposed, skipping circle update for map {MapId}", MapId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update circles for map {MapId}", MapId);
        }
    }

    /// <summary>
    /// Binds the map's element id and style from the view model, generating a unique id when none
    /// is supplied.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Id, x => x.MapId, (o, curr) =>
        {
            if (curr != null)
                return curr;
            var toString = o?.ToString();
            return string.IsNullOrWhiteSpace(toString) ? $"apple-map-{Guid.NewGuid().AsString()}" : toString;
        });

        DataBind(ViewModel.Style, x => x.Style);
    }

    /// <summary>
    /// JavaScript-invokable callback raised when a marker or circle is clicked; posts a
    /// <c>ClickedEvent</c> carrying the element id to the hub.
    /// </summary>
    /// <param name="id">Identifier of the clicked marker or circle.</param>
    [JSInvokable]
    public void OnClicked(string id)
    {
        var clickedEvent = new ClickedEvent(Area, Stream!.StreamId)
        {
            Payload = id
        };
        Hub.Post(clickedEvent, o => o.WithTarget(Stream.Owner));
    }
}
