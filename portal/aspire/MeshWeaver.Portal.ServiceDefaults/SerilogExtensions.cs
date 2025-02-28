using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Hosting;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using NpgsqlTypes;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Sinks.PostgreSQL;

namespace MeshWeaver.Portal.ServiceDefaults;

public static class SerilogExtensions
{
    public static MeshHostApplicationBuilder AddPostgresSerilog(this MeshHostApplicationBuilder builder)
    {
        var messageDeliveryPolicy = new DestructuringPolicy();
        builder.ConfigureHub(h =>
            h.WithInitialization(hub => messageDeliveryPolicy.JsonOptions = hub.JsonSerializerOptions));

        var deliveryColumns = new Dictionary<string, ColumnWriterBase>
        {
            // Timestamp from log event
            {"timestamp", new TimestampColumnWriter()},
            {"level", new LevelColumnWriter(true, NpgsqlDbType.Varchar)},
            {"address", new SinglePropertyColumnWriter("Address", PropertyWriteMethod.Raw)},
            {"delivery", new SinglePropertyColumnWriter("Delivery", PropertyWriteMethod.Json, NpgsqlDbType.Jsonb)},
        };

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            // Add destructuring policies
            .Destructure.With(messageDeliveryPolicy)
            .Destructure.ToMaximumDepth(4)
            .Destructure.ToMaximumStringLength(1000)
            .Destructure.ToMaximumCollectionCount(20)
            // Filter to only include MessageService delivery logs
            .Filter.ByIncludingOnly(Filter)
            .WriteTo.PostgreSQL(
                connectionString: builder.Host.Configuration.GetConnectionString("meshweaverdb"),
                tableName: "messages",
                columnOptions: deliveryColumns,
                needAutoCreateTable: true,
                schemaName: "public")
            .Enrich.FromLogContext()
            .CreateLogger();

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
        if (node is JsonObject obj)
            return new StructureValue(obj.Select(x => new LogEventProperty(x.Key, Parse(x.Value))));
        if (node is JsonArray arr)
            return new SequenceValue(arr.Select(Parse));
        return new ScalarValue(node.ToString());
    }
}
