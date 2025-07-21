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
    public static MeshHostApplicationBuilder AddEfCoreSerilog(this MeshHostApplicationBuilder builder, string service, string serviceId)
    {
        var sink = new EfCoreSystemLogSink(service, serviceId);
        var messageDeliveryPolicy = new DestructuringPolicy();
        builder.ConfigureHub(h =>
            h.WithInitialization(hub =>
            {
                messageDeliveryPolicy.JsonOptions = hub.JsonSerializerOptions;
                sink.Initialize(hub.ServiceProvider);
            }));

        // Create Serilog logger configuration
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .Destructure.With(messageDeliveryPolicy)
            .Destructure.ToMaximumDepth(4)
            .Destructure.ToMaximumStringLength(1000)
            .Destructure.ToMaximumCollectionCount(20)
            ;


        // Create logger and register
        var logger = loggerConfiguration.CreateLogger();


        // Add Serilog to the logging pipeline
        builder.Host.Logging.AddSerilog(logger);

        return builder;
    }
    public static MeshHostApplicationBuilder AddEfCoreMessageLog(this MeshHostApplicationBuilder builder, string service, string serviceId)
    {
        var sink = new EfCoreMessageLogSink(service, serviceId);
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
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .Destructure.With(messageDeliveryPolicy)
            .Destructure.ToMaximumDepth(4)
            .Destructure.ToMaximumStringLength(1000)
            .Destructure.ToMaximumCollectionCount(20)
            .Filter.ByIncludingOnly(Matching.FromSource("MeshWeaver.Messaging.MessageService"));



        // Create logger and register
        var logger = loggerConfiguration.CreateLogger();


        // Add Serilog to the logging pipeline
        builder.Host.Logging.AddSerilog(logger);

        return builder;
    }
    public static IReadOnlyDictionary<string, object?> ToJsonDictionary(this StructureValue structuredValue)
    {
        var dictionary = structuredValue.Properties.ToDictionary(p => p.Name, p => ConvertToJsonCompatibleValue(p.Value));
        return dictionary;
    }

    public static object? ConvertToJsonCompatibleValue(this LogEventPropertyValue value)
    {
        return value switch
        {
            StructureValue sv => sv.Properties.ToDictionary(p => p.Name, p => ConvertToJsonCompatibleValue(p.Value)),
            SequenceValue seq => seq.Elements.Select(ConvertToJsonCompatibleValue).ToList(),
            ScalarValue scalar => scalar.Value,
            _ => value.ToString()
        };
    }
    public static IReadOnlyDictionary<string, object?> ConvertToJsonCompatibleValue(this IReadOnlyDictionary<string, LogEventPropertyValue> dict)
    {
        return dict.ToDictionary(x => x.Key, x => x.Value.ConvertToJsonCompatibleValue());
    }

}
public class EfCoreSystemLogSink(string service, string id) : ILogEventSink
{
    private IServiceProvider serviceProvider = null!;

    public void Initialize(IServiceProvider serviceProvider)
        => this.serviceProvider = serviceProvider;

    public void Emit(LogEvent logEvent)
    {

        var logEntry = new SystemLog(
            service,
            id,
            logEvent.Level.ToString(),
            logEvent.Timestamp.UtcDateTime,
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString(),
            logEvent.Properties.ConvertToJsonCompatibleValue()!
        );

        try
        {
            using var dbContext = serviceProvider.CreateScope().ServiceProvider.GetRequiredService<MeshWeaverDbContext>();

            dbContext.SystemLogs.Add(logEntry);
            dbContext.SaveChanges();
        }
        catch
        {
        }
    }

}
public class EfCoreMessageLogSink(string service, string serviceId) : ILogEventSink
{
    private IServiceProvider serviceProvider = null!;

    public void Initialize(IServiceProvider serviceProvider)
        => this.serviceProvider = serviceProvider;

    public void Emit(LogEvent evt)
    {

        if (!Matching.FromSource<MessageService>()(evt))
            return;
        if (!evt.Properties.TryGetValue("Delivery", out var deliveryStructured))
            return;
        if (deliveryStructured is not StructureValue delivery)
            return;


        var deliveryProps = delivery.Properties.ToDictionary(x => x.Name, x => x.Value);
        var target = deliveryProps.GetValueOrDefault("target")?.ToString();
        if (string.IsNullOrWhiteSpace(target))
            return;

        // Find the message property in the delivery structure
        var messageProp = deliveryProps.GetValueOrDefault("message");
        if (messageProp is not StructureValue messageValue)
            return;

        // Check if it has a $type property indicating ExecutionRequest
        var typeProp = messageValue.Properties.FirstOrDefault(p => p.Name == "$type");
        if (typeProp?.Value.ToString().Contains("ExecutionRequest") == true)
            return;



        var logEntry = new MessageLog(
            service,
            serviceId,
            evt.Timestamp.UtcDateTime,
            evt.Properties.GetValueOrDefault("Address")?.ToString() ?? string.Empty,
            deliveryProps.GetValueOrDefault("id")?.ToString() ?? string.Empty,
            messageValue.ToJsonDictionary(),
            deliveryProps.GetValueOrDefault("sender")?.ToString(),
            target,
            deliveryProps.GetValueOrDefault("state")?.ToString(),
            (deliveryProps.GetValueOrDefault("accessContext") as StructureValue)?.ToJsonDictionary(),
            (deliveryProps.GetValueOrDefault("properties") as StructureValue)?.ToJsonDictionary()

        );
        try
        {
            using var dbContext = serviceProvider.CreateScope().ServiceProvider.GetRequiredService<MeshWeaverDbContext>();

            dbContext.Messages.Add(logEntry);
            dbContext.SaveChanges();
        }
        catch { }
    }
}
public class DestructuringPolicy : IDestructuringPolicy
{
    public JsonSerializerOptions JsonOptions { get; set; } = new();

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyFactory, out LogEventPropertyValue result)
    {
        if (value is IMessageDelivery delivery and { Message: not ExecutionRequest })
        {
            // Use the configured JSON serializer with hub options
            var node = JsonSerializer.SerializeToNode(delivery, JsonOptions);
            result = Parse(node);
            return true;
        }

        result = null!;
        return false;
    }

    LogEventPropertyValue Parse(JsonNode? node)
    {
        if (node is null)
            return new ScalarValue(null);
        if (node is JsonObject obj)
            return new StructureValue(obj.Select(x => new LogEventProperty(x.Key, Parse(x.Value))));
        if (node is JsonArray arr)
            return new SequenceValue(arr.Select(Parse));
        return new ScalarValue(node.ToString());
    }
}
