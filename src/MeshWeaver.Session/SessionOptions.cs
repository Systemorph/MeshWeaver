namespace MeshWeaver.Session;

public record SessionOptions 
{
    public string SessionId { get; set; }
    public Guid SessionIdGuid => SessionId == null ? Guid.Empty : new Guid(SessionId);
    public string ProjectId { get; set; }
    public string Environment { get; set; }
    public string NotebookId { get; set; }
    public string MountPath { get; set; }
    public string User { get; set; }
    public string SessionType { get; set; }

    public int ProgressReportInterval { get; set; } = 100;
    public string NotebookEditorPath => $"{ProjectId}/{Environment}/{NotebookId}";
    public string ProjectEnvironmentPath => $"{ProjectId}/{Environment}";
}
