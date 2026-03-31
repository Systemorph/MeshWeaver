using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for ImportLayoutArea - the import form with namespace picker,
/// source type selector, and conditional source input.
/// </summary>
public class ImportLayoutAreaTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ImportView = "ImportMeshNodes";

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        return conf
            .WithServices(services => services
                .AddInMemoryPersistence())
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, ConfigureHost)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(ImportView, ImportLayoutArea.ImportMeshNodes));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task ImportForm_RendersStackControl()
    {
        var reference = new LayoutAreaReference(ImportView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<StackControl>();
    }

    [HubFact]
    public async Task ImportForm_HasMultipleAreas()
    {
        var reference = new LayoutAreaReference(ImportView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        // Should have: H2 header, namespace picker, source radio group,
        // conditional source section, cancel button
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(4,
            "should have header, namespace picker, source selector, conditional section, and cancel button");
    }

    [HubFact]
    public async Task ImportForm_HasNamespacePicker()
    {
        var reference = new LayoutAreaReference(ImportView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var stack = (StackControl)control!;
        var areas = stack.Areas.ToList();

        // Find MeshNodePickerControl for namespace (second area after header)
        var foundPicker = false;
        foreach (var area in areas)
        {
            var areaName = area.Area?.ToString();
            if (string.IsNullOrEmpty(areaName)) continue;

            var areaControl = await stream
                .GetControlStream(areaName)
                .Timeout(3.Seconds())
                .FirstAsync(x => x != null);

            if (areaControl is MeshNodePickerControl picker && picker.Label?.ToString() == "Destination Namespace")
            {
                foundPicker = true;
                break;
            }
        }

        foundPicker.Should().BeTrue("should contain a MeshNodePickerControl for destination namespace");
    }

    [HubFact]
    public async Task ImportForm_HasSourceTypeRadioGroup()
    {
        var reference = new LayoutAreaReference(ImportView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var stack = (StackControl)control!;
        var areas = stack.Areas.ToList();

        // Find the area containing the radio group (inside a nested stack)
        var foundRadio = false;
        foreach (var area in areas)
        {
            var areaName = area.Area?.ToString();
            if (string.IsNullOrEmpty(areaName)) continue;

            var areaControl = await stream
                .GetControlStream(areaName)
                .Timeout(3.Seconds())
                .FirstAsync(x => x != null);

            if (areaControl is RadioGroupControl)
            {
                foundRadio = true;
                break;
            }

            // Radio might be inside a nested StackControl (with label)
            if (areaControl is StackControl nestedStack)
            {
                foreach (var nestedArea in nestedStack.Areas)
                {
                    var nestedName = nestedArea.Area?.ToString();
                    if (string.IsNullOrEmpty(nestedName)) continue;

                    var nestedControl = await stream
                        .GetControlStream(nestedName)
                        .Timeout(3.Seconds())
                        .FirstAsync(x => x != null);

                    if (nestedControl is RadioGroupControl)
                    {
                        foundRadio = true;
                        break;
                    }
                }
                if (foundRadio) break;
            }
        }

        foundRadio.Should().BeTrue("should contain a RadioGroupControl for source type selection");
    }

    [HubFact]
    public async Task ImportForm_HasCancelButton()
    {
        var reference = new LayoutAreaReference(ImportView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var stack = (StackControl)control!;
        var areas = stack.Areas.ToList();

        var foundCancel = false;
        foreach (var area in areas)
        {
            var areaName = area.Area?.ToString();
            if (string.IsNullOrEmpty(areaName)) continue;

            var areaControl = await stream
                .GetControlStream(areaName)
                .Timeout(3.Seconds())
                .FirstAsync(x => x != null);

            if (areaControl is ButtonControl button && button.Data?.ToString() == "Cancel")
            {
                foundCancel = true;
                break;
            }
        }

        foundCancel.Should().BeTrue("should have a Cancel button");
    }
}
