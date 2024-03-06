using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Scope;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.Scopes;

namespace OpenSmc.Layout.Test;
public record ChangeDataRecordRequest;
public class TestLayoutPlugin(IMessageHub hub) : MessageHubPlugin(hub),
    IMessageHandler<ChangeDataRecordRequest>
{

    public const string MainStackId = nameof(MainStackId);
    public const string NamedArea = nameof(NamedArea);
    public const string UpdatingView = nameof(UpdatingView);
    public const string SomeString = nameof(SomeString);
    public const string NewString = nameof(NewString);
    public const string DataBoundView = nameof(DataBoundView);

    public interface ITestScope : IMutableScope
    {
        int Integer { get; set; }
        double Double { get; set; }

        [DefaultValue(SomeString)]
        string String { get; set; }
    }

    public record DataRecord([property: Key]string SystemName, string DisplayName);


    private IApplicationScope applicationScope = hub.ServiceProvider.GetRequiredService<IApplicationScope>();
    private readonly IWorkspace workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();

    public LayoutDefinition Configure(LayoutDefinition layout)
        => layout
            .WithInitialState(Controls.Stack()
                .WithId(MainStackId)
                .WithClickAction(context =>
                {
                    context.Hub.Post(new SetAreaRequest(TestAreas.NewArea,
                        Controls.TextBox("Hello")
                            .WithId("HelloId")));
                    return Task.CompletedTask;
                })
            )
            .WithView(NamedArea, _ =>
                Controls.TextBox(NamedArea)
                    .WithId(NamedArea)
            )
            // this tests proper updating in the case of MVP
            .WithView(UpdatingView, _ => ModelViewPresenterTestCase())
            // this tests proper updating in the case of MVP
            .WithView(DataBoundView, _ =>
                Template.Bind(workspace.GetData<DataRecord>().First(),
                    record =>
                        Controls
                            .Menu(record.DisplayName)
                            .WithClickMessage(new ChangeDataRecordRequest(), Hub.Address)
                            .WithId(DataBoundView)
                        )
            )
            .WithInitialization(_ => workspace.Initialized);


    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await workspace.Initialized;
        await base.StartAsync(cancellationToken);
    }

    public string DataBindClicked;

    private object ModelViewPresenterTestCase()
    {
        return Controls.TextBox(applicationScope.GetScope<ITestScope>().String)
            .WithId(UpdatingView)
            .WithClickAction(_ => applicationScope.GetScope<ITestScope>().String = NewString);
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<ChangeDataRecordRequest> request)
    {
        workspace.Update(workspace.GetData<DataRecord>().First() with {DisplayName = NewString});
        workspace.Commit();
        return request.Processed();
    }
}

