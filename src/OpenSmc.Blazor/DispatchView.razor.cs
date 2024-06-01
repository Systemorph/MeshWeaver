using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public partial class DispatchView
{
    [Inject] private IMessageHub Hub { get; set; }
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        var client = Hub.ServiceProvider.GetRequiredService<IBlazorClient>();
        ViewDescriptor = client.GetViewDescriptor(ViewModel, Stream);
    }
}
