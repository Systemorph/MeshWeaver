using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Filters;

namespace MeshWeaver.Portal.ServiceDefaults;

public static class SerilogExtensions
{
    public static MeshHostApplicationBuilder AddEfCoreSerilog(this MeshHostApplicationBuilder builder)
    {
        var sink = new EfCoreSink();
        var messageDeliveryPolicy = new DestructuringPolicy();
        builder.ConfigureHub(h =>
            h.WithInitialization(hub =>
            {
                messageDeliveryPolicy.JsonOptions = hub.JsonSerializerOptions;
                sink.Initialize(hub.ServiceProvider);
            }));

        // Create Serilog logger configuration
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .Destructure.With(messageDeliveryPolicy)
            .Destructure.ToMaximumDepth(4)
            .Destructure.ToMaximumStringLength(1000)
            .Destructure.ToMaximumCollectionCount(20)
            // Filter to only include MessageService delivery logs
            .Filter.ByIncludingOnly(Filter);

        // Create logger and register
        var logger = loggerConfiguration.CreateLogger();


        // Add Serilog to the logging pipeline
        builder.Host.Logging.AddSerilog(logger);

        return builder;
    }

    private static bool Filter(LogEvent evt)
    {
        if (!Matching.FromSource<MessageService>()(evt))
            return false;
        if (!evt.Properties.TryGetValue("Delivery", out var lepv))
            return false;
        if (lepv is not StructureValue sv)
            return false;

        // Find the message property in the delivery structure
        var messageProp = sv.Properties.FirstOrDefault(p => p.Name == "message");
        if (messageProp?.Value is not StructureValue messageValue)
            return false;

        // Check if it has a $type property indicating ExecutionRequest
        var typeProp = messageValue.Properties.FirstOrDefault(p => p.Name == "$type");
        if (typeProp?.Value.ToString().Contains("ExecutionRequest") == true)
            return false;

        return true;
    }
}
public class EfCoreSink : ILogEventSink
{
    private IServiceProvider serviceProvider;

    public void Initialize(IServiceProvider serviceProvider)
        => this.serviceProvider = serviceProvider;
 
    public void Emit(LogEvent logEvent)
    {
        using var dbContext = serviceProvider.CreateScope().ServiceProvider.GetRequiredService<MeshWeaverDbContext>();
        if (dbContext is null)
            return;

        var logEntry = new MessageLog(
            logEvent.Level.ToString(), 
            logEvent.Timestamp.UtcDateTime, 
            logEvent.RenderMessage(), 
            logEvent.Exception?.ToString(),
            JsonSerializer.Serialize(logEvent.Properties.ToDictionary(x => x.Key, x => x.Value.ToString()))
        );

        dbContext.Messages.Add(logEntry);
        dbContext.SaveChanges();
    }
}
public class DestructuringPolicy : IDestructuringPolicy
{
    public JsonSerializerOptions JsonOptions { get; set; }

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyFactory, out LogEventPropertyValue result)
    {
        if (value is IMessageDelivery delivery and { Message: not ExecutionRequest })
        {
            // Use the configured JSON serializer with hub options
            var node = JsonSerializer.SerializeToNode(delivery, JsonOptions);
            result = Parse(node);
            return true;
        }

        result = null;
        return false;
    }

    LogEventPropertyValue Parse(JsonNode node)
    {
        if (node is null)
            return null;
        if (node is JsonObject obj)
            return new StructureValue(obj.Select(x => new LogEventProperty(x.Key, Parse(x.Value))));
        if (node is JsonArray arr)
            return new SequenceValue(arr.Select(Parse));
        return new ScalarValue(node.ToString());
    }
}
