namespace Memex.Client.Voice;

/// <summary>A piece of transcribed text with its time range.</summary>
public readonly record struct TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
