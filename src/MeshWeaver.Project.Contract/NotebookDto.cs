using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Project.Contract;

public record GetNotebookRequest : IRequest<NotebookDto>
{
    public bool IncludeElements { get; init; }
}

public record NotebookDto(string Id, string Name)
{
    public NotebookDto()
        : this(default, default)
    {
    }

    public NotebookDto(string Id, string Name, string folder, EvaluationStatus evaluationStatus, int version)
        : this(Id, Name)
    {
        Folder = folder;
        EvaluationStatus = evaluationStatus;
        Version = version;
    }

    public NotebookDto(NotebookDocument doc)
        :this(doc.Id, doc.Name)
    {
        // TODO SMCv2: What is folder and how to populate?! (2023/09/13, Roland Buergi)

        CreatedOn = doc.CreatedOn;
        LastModified = doc.LastModified;
        Metadata = doc.Metadata;
        ElementIds = doc.ElementIds;
        Version = doc.Version;

    }

    public string Folder { get; init; } = "";
    public DateTime CreatedOn { get; init; } = DateTime.UtcNow;
    public DateTime LastModified { get; init; } = DateTime.UtcNow;
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = ImmutableDictionary<string,object>.Empty;
    // will not be shipped
    public IReadOnlyCollection<NotebookElementDto> Elements { get; init; } = Array.Empty<NotebookElementDto>();
    public IReadOnlyCollection<string> ElementIds { get; init; } = ImmutableList<string>.Empty;

    // TODO SMCv2: We should not be populating this from here ==> consider moving to different place (2023/09/13, Roland Buergi)
    public EvaluationStatus EvaluationStatus { get; init; }
    public int Version { get; init; }
}

public record NotebookElementContentDto(string Id, string ElementKind)
{
    public string Content { get; init; }
    public string Language { get; init; }
}

public record NotebookElementDto(string Id, string ElementKind) : NotebookElementContentDto(Id, ElementKind)
{
    public Dictionary<string, object> Metadata { get; init; } = new();

    // TODO SMCv2: Remove from here and make dedicated control (2023/08/04, Roland Buergi)
    // Most likely array of redirect controls
    public object[] Outputs { get; init; } = Array.Empty<object>();
    public EvaluationStatus? EvaluationStatus { get; init; }

    public TimeSpan? EvaluationTime { get; init; }

    public string EvaluationError { get; init; }
    public int? EvaluationCount { get; init; }
    public int Version { get; init; }
}

public enum EvaluationStatus
{
    Idle,
    Pending,
    Evaluating
}

