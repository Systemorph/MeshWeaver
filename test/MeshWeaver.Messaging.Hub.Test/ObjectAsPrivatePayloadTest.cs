using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

public class ObjectAsPrivatePayloadTest
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    public void FailedReadReportsTheFailureWithoutPublishingContentOrExceptionText(
        bool node, bool runtimeType, int failure)
    {
        // 🚨 THE CONTENT AND THE SUBJECT ARE TWO DIFFERENT STRINGS, and this test used to use ONE
        // (Systemorph/MeshWeaver#4597). It passed the private text as the `what` argument and then
        // asserted the log did not contain it — which reads as "content is not published" and is
        // actually "the caller-supplied subject is dropped". Those are not the same claim, and only
        // the first is this accessor's contract: `what` is documented as "a node path, a control
        // name", `ContentAs<T>` passes `node.Path` into it, and the accessor's OTHER two report sites
        // have always printed it as `{What}`. So a caller who puts content there was already
        // publishing it through those branches, and dropping it here bought no privacy — it only made
        // the one remaining line unattributable, which is the whole of #4597's open half.
        //
        // With the two separated the test says both things, and the privacy half is now tested
        // against a string that is genuinely CONTENT rather than one the caller chose to name.
        const string privateText = "synthetic-private-correspondence";
        const string subject = "acme/Drafts/QuarterlyNote";
        var json = JsonSerializer.Serialize(new { message = privateText });
        object value = node ? JsonNode.Parse(json)! : JsonSerializer.Deserialize<JsonElement>(json);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new RejectPayload(failure));
        var logger = new CapturingLogger();

        var result = runtimeType
            ? value.As(typeof(Payload), options, logger, subject)
            : value.As<Payload>(options, logger, subject);

        result.Should().BeNull();
        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Error);
        entry.Text.Should().NotContain(privateText);
        entry.Exception.Should().BeNull("converter exceptions may contain the entire private value");
        entry.Text.Should().Contain(nameof(Payload));
        entry.Text.Should().Contain(subject,
            "a recovery failure that names no subject cannot be followed up — #4597 carried exactly "
            + "that for twelve days");
        entry.Text.Should().Contain(failure switch
        {
            0 => nameof(JsonException),
            1 => nameof(NotSupportedException),
            _ => nameof(InvalidOperationException),
        });
    }

    private sealed record Payload(string Message);

    private sealed class RejectPayload(int failure) : JsonConverter<Payload>
    {
        public override Payload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var content = document.RootElement.GetRawText();
            throw failure switch
            {
                0 => new JsonException(content),
                1 => new NotSupportedException(content),
                _ => new InvalidOperationException(content),
            };
        }

        public override void Write(Utf8JsonWriter writer, Payload value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
