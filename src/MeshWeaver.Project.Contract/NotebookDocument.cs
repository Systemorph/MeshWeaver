using System.Collections.Immutable;

namespace MeshWeaver.Project.Contract
{
    public record NotebookDocument(string Id, string Name, DateTime CreatedOn, DateTime LastModified)
    {
        public int Version { get; init; }
        public IReadOnlyDictionary<string, object> Metadata { get; init; } = ImmutableDictionary<string, object>.Empty;
        public ImmutableList<string> ElementIds { get; init; } = ImmutableList<string>.Empty;
    }

    public record NotebookElement(string Id, string ElementKind) 
    {
        public string Language { get; init; }
        public IReadOnlyDictionary<string, object> Metadata { get; init; } = ImmutableDictionary<string, object>.Empty;
        public string Content { get; init; }
        public int Version { get; init; }
        public EvaluationStatus EvaluationStatus { get; init; }
        public string EvaluationError { get; init; }
        public TimeSpan? EvaluationTime { get; init; }
        public int? EvaluationCount { get; init; }
    }

    public record NotebookOutputElement(string Id, string ElementId, object Presenter) 
    {
        public int Version { get; init; }
    }

    public static class NotebookElementKind
    {
        public const string Markdown = "markdown";
        public const string Code = "code";
    }
    public static class NotebookLanguages
    {
        public const string CSharp = "csharp";
    }
}
