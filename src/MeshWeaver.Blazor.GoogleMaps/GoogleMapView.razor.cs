using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Layout;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Blazor.GoogleMaps;

public partial class GoogleMapView : BlazorView<GoogleMapControl, GoogleMapView>
{
    [Inject] private IOptions<GoogleMapsConfiguration> Configuration { get; set; } = null!;

    private string ApiKey => Configuration.Value.ApiKey ?? "";
    private string MapId { get; set; } = null!;
    private object? Style { get; set; }
    private IJSObjectReference? jsModule;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            Logger.LogDebug("OnAfterRenderAsync - first render");
            Logger.LogDebug("API Key: {ApiKeyStatus}", string.IsNullOrEmpty(ApiKey) ? "MISSING" : "Present");
            
            try
            {
                Logger.LogDebug("Loading JavaScript module...");
                jsModule = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/MeshWeaver.Blazor.GoogleMaps/GoogleMapView.razor.js");
                Logger.LogDebug("JavaScript module loaded successfully");
                
                await InitializeMap();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in Initializing map");
            }
        }

        try
        {
            await UpdateMarkers();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in UpdateMarkers");
        }
        await base.OnAfterRenderAsync(firstRender);
    }


    private async Task InitializeMap()
    {
        try
        {
            Logger.LogDebug("InitializeMap starting...");
            
            if (jsModule == null)
            {
                Logger.LogError("JavaScript module not loaded");
                return;
            }
            
            // Initialize the map (JavaScript will handle Google Maps API loading)
            Logger.LogDebug("Initializing map with ID: {MapId}", MapId);
            
            var mapOptions = GetMapOptions();
            Logger.LogDebug("Map options: {@MapOptions}", mapOptions);
            
            // Pass API key to JavaScript for dynamic loading and register marker click callback
            await jsModule.InvokeVoidAsync("initializeMap", MapId, mapOptions, ApiKey);
            
            // Small delay to let map initialization settle
            await Task.Delay(200);
            
            await jsModule.InvokeVoidAsync("setMarkerClickCallback", MapId, DotNetObjectReference.Create(this));
            Logger.LogDebug("Map initialized successfully");
            
            StateHasChanged();
            Logger.LogDebug("Map loading complete");
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
            Logger.LogError(ex, "Failed to initialize Google Map");
        }
    }

    private async Task UpdateMarkers()
    {
        if (string.IsNullOrEmpty(MapId) || ViewModel.Markers == null || jsModule == null || !ViewModel.Markers.Any())
            return;

        try
        {
            // Check if the JavaScript module is still valid before calling
            var markerConfigs = ViewModel.Markers.Select(m => new
            {
                id = m.Id ?? Guid.NewGuid().ToString(),
                position = new { lat = m.Position.Lat, lng = m.Position.Lng },
                title = m.Title ?? "",
                label = m.Label ?? "",
                draggable = m.Draggable,
                icon = m.Icon
            }).ToArray();

            // Add a small delay to ensure map is ready for marker updates
            await Task.Delay(100);
            
            await jsModule.InvokeVoidAsync("updateMarkers", MapId, markerConfigs);
            
            Logger.LogDebug("Successfully updated {MarkerCount} markers for map {MapId}", markerConfigs.Length, MapId);
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

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Id, x => x.MapId, (o, curr) =>
        {
            if (curr != null)
                return curr;
            var toString = o?.ToString();
            return string.IsNullOrWhiteSpace(toString) ? $"google-map-{Guid.NewGuid().AsString()}" : toString;
        });

        DataBind(ViewModel.Style, x => x.Style);
    }



    private object GetMapOptions()
    {
        var controlOptions = ViewModel.Options;
        
        return new
        {
            zoom = controlOptions?.Zoom ?? 10,
            center = controlOptions?.Center != null 
                ? new { lat = controlOptions.Center.Lat, lng = controlOptions.Center.Lng }
                : new { lat = 0.0, lng = 0.0 },
            mapTypeId = controlOptions?.MapTypeId switch
            {
                "satellite" => "satellite",
                "hybrid" => "hybrid", 
                "terrain" => "terrain",
                _ => "roadmap"
            },
            disableDefaultUI = controlOptions?.DisableDefaultUI ?? false,
            zoomControl = controlOptions?.ZoomControl ?? true,
            mapTypeControl = controlOptions?.MapTypeControl ?? true,
            scaleControl = controlOptions?.ScaleControl ?? false,
            streetViewControl = controlOptions?.StreetViewControl ?? true,
            rotateControl = controlOptions?.RotateControl ?? false,
            fullscreenControl = controlOptions?.FullscreenControl ?? false
        };
    }

    [JSInvokable]
    public void OnMarkerClicked(string markerId)
    {
        // Post ClickedEvent to the hub with markerId as payload
        var clickedEvent = new ClickedEvent(Area, Stream!.StreamId)
        {
            Payload = markerId
        };

        // Post the event through the Hub
        Hub.Post(clickedEvent, o => o.WithTarget(Stream.Owner));
    }

    public override async ValueTask DisposeAsync()
    {
        if (jsModule is not null)
        {
            await jsModule.DisposeAsync();
        }
        
        await base.DisposeAsync();
    }
}
