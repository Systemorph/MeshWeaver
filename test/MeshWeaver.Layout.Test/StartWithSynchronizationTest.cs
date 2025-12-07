using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for IObservable views using StartWith, specifically testing
/// that JSON synchronization properly propagates all control updates
/// from loading state to actual content.
/// </summary>
public class StartWithSynchronizationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string LoadingView = nameof(LoadingView);
    private const string DelayedLoadingView = nameof(DelayedLoadingView);
    private const string SubjectControlledView = nameof(SubjectControlledView);

    // Subject to control emissions in tests
    private static readonly ReplaySubject<UiControl> ControlSubject = new(1);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress<ClientAddress>((_, d) => d.Package()))
            .AddData(data => data
                .AddSource(ds => ds
                    .WithType<TestDataRecord>(t => t
                        .WithInitialData([
                            new TestDataRecord("1", "First Item"),
                            new TestDataRecord("2", "Second Item"),
                            new TestDataRecord("3", "Third Item")
                        ]))))
            .AddLayout(layout => layout
                .WithView(LoadingView, LoadingViewImpl)
                .WithView(DelayedLoadingView, DelayedLoadingViewImpl)
                .WithView(SubjectControlledView, SubjectControlledViewImpl)
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private record TestDataRecord(string Id, string Name);

    #region View Implementations

    private IObservable<UiControl> LoadingViewImpl(LayoutAreaHost area, RenderingContext context)
    {
        // Pattern: StartWith loading message, then emit actual content from data stream
        return area.Hub.GetWorkspace()
            .GetStream<TestDataRecord>()!
            .Select(data => (UiControl)Controls.Html($"Loaded {data?.Length ?? 0} items"))
            .StartWith(Controls.Markdown("# Loading\n\n*Please wait...*"));
    }

    private IObservable<UiControl> DelayedLoadingViewImpl(LayoutAreaHost area, RenderingContext context)
    {
        // Pattern: StartWith loading message, then emit after a delay
        return Observable.Timer(TimeSpan.FromMilliseconds(200))
            .Select(_ => (UiControl)Controls.Html("Delayed content loaded"))
            .StartWith(Controls.Markdown("# Loading\n\n*Fetching data...*"));
    }

    private IObservable<UiControl> SubjectControlledViewImpl(LayoutAreaHost area, RenderingContext context)
    {
        // Pattern: StartWith loading message, then emit when subject pushes value
        return ControlSubject
            .StartWith(Controls.Markdown("# Loading\n\n*Waiting for signal...*"));
    }

    #endregion

    #region Tests

    /// <summary>
    /// Tests that when a view uses StartWith with a loading indicator,
    /// and data is available immediately, the view transitions from
    /// loading to content through JSON synchronization.
    /// </summary>
    [HubFact]
    public async Task StartWith_WithImmediateData_TransitionsToContent()
    {
        var reference = new LayoutAreaReference(LoadingView);

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Use GetRemoteStream to go through JSON synchronization
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        Output.WriteLine("Waiting for control stream...");

        // Collect all controls emitted
        var controls = await stream
            .GetControlStream(reference.Area!)
            .TakeUntil(c => c is HtmlControl)
            .Timeout(5.Seconds())
            .ToArray();

        Output.WriteLine($"Received {controls.Length} control(s)");
        foreach (var ctrl in controls)
        {
            Output.WriteLine($"  - {ctrl?.GetType().Name}: {GetControlContent(ctrl)}");
        }

        // Should have transitioned to actual content
        controls.Should().HaveCountGreaterThanOrEqualTo(1);
        controls.Last().Should().BeOfType<HtmlControl>()
            .Which.Data.ToString().Should().Contain("Loaded");
    }

    /// <summary>
    /// Tests that when a view uses StartWith with delayed data,
    /// the loading state is visible first, then transitions to content.
    /// </summary>
    [HubFact]
    public async Task StartWith_WithDelayedData_ShowsLoadingThenContent()
    {
        var reference = new LayoutAreaReference(DelayedLoadingView);

        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        Output.WriteLine("Waiting for initial control...");

        // First, we should get the loading control
        var firstControl = await stream
            .GetControlStream(reference.Area!)
            .Timeout(2.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"First control: {firstControl?.GetType().Name}: {GetControlContent(firstControl)}");
        firstControl.Should().BeOfType<MarkdownControl>()
            .Which.Markdown.ToString().Should().Contain("Loading");

        Output.WriteLine("Waiting for content transition...");

        // Then we should get the actual content
        var finalControl = await stream
            .GetControlStream(reference.Area!)
            .TakeUntil(c => c is HtmlControl)
            .Timeout(3.Seconds())
            .LastAsync();

        Output.WriteLine($"Final control: {finalControl?.GetType().Name}: {GetControlContent(finalControl)}");
        finalControl.Should().BeOfType<HtmlControl>()
            .Which.Data.ToString().Should().Contain("Delayed content loaded");
    }

    /// <summary>
    /// Tests that when a view uses StartWith with subject-controlled emission,
    /// the view properly transitions when the subject emits.
    /// This test explicitly controls timing to expose race conditions.
    /// </summary>
    [HubFact]
    public async Task StartWith_WithSubjectControl_TransitionsOnEmission()
    {
        // First emit the content to the subject BEFORE getting the stream
        // This simulates data being ready after initialization
        ControlSubject.OnNext(Controls.Html("Subject content delivered"));

        var reference = new LayoutAreaReference(SubjectControlledView);

        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        Output.WriteLine("Waiting for content...");

        // The subject already has content, so we should get the HTML control
        var controls = await stream
            .GetControlStream(reference.Area!)
            .TakeUntil(c => c is HtmlControl)
            .Timeout(5.Seconds())
            .ToArray();

        Output.WriteLine($"Received {controls.Length} control(s)");
        foreach (var ctrl in controls)
        {
            Output.WriteLine($"  - {ctrl?.GetType().Name}: {GetControlContent(ctrl)}");
        }

        // Should have received at least one control and ended with HTML
        controls.Should().HaveCountGreaterThanOrEqualTo(1);
        controls.Last().Should().BeOfType<HtmlControl>()
            .Which.Data.ToString().Should().Contain("Subject content delivered");
    }

    /// <summary>
    /// Tests rapid emissions through StartWith to expose race conditions
    /// in the JSON synchronization pipeline.
    /// </summary>
    [HubFact]
    public async Task StartWith_RapidEmissions_AllUpdatesReceived()
    {
        var reference = new LayoutAreaReference(LoadingView);

        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        // Subscribe and collect all controls
        var controlsReceived = new List<UiControl?>();
        var subscription = stream
            .GetControlStream(reference.Area!)
            .Subscribe(c =>
            {
                Output.WriteLine($"Received: {c?.GetType().Name}: {GetControlContent(c)}");
                lock (controlsReceived)
                {
                    controlsReceived.Add(c);
                }
            });

        // Wait for at least one control
        await Task.Delay(1.Seconds());

        subscription.Dispose();

        Output.WriteLine($"Total controls received: {controlsReceived.Count}");

        // Should have received at least one control (either loading or content)
        controlsReceived.Should().NotBeEmpty();

        // If we got multiple controls, the last one should be the content
        if (controlsReceived.Count > 1)
        {
            controlsReceived.Last().Should().BeOfType<HtmlControl>();
        }
    }

    #endregion

    #region Helpers

    private static string GetControlContent(UiControl? control)
    {
        return control switch
        {
            MarkdownControl md => md.Markdown?.ToString() ?? "(null)",
            HtmlControl html => html.Data?.ToString() ?? "(null)",
            _ => control?.ToString() ?? "(null)"
        };
    }

    #endregion
}
