namespace OpenSmc.Project.Contract;

public abstract record ProjectNode(string Id, string Name, string ParentId)
{
    public int Version { get; init; }
}


public record NotebookNode(string Id /* guid.AsString()*/, string Name, string ParentId) : ProjectNode(Id, Name, ParentId);

public record FolderNode(string Id, string Name, string ParentId) : ProjectNode(Id, Name, ParentId)
{
    public const string RootId = "_root";
    public const string RootName = "/";

    public static FolderNode Root => new(RootId, RootName, null);
}

public record BlobNode(string Id, string Name, string ParentId) : ProjectNode(Id, Name, ParentId);

public record EnvironmentInfo(string Id, string ProjectId, string BasePath)
{
    public Dictionary<string, string> SecretsMapping { get; init; }
    public string KeyVault { get; init; } // TODO V10: Complete this (2022-04-11, Andrei Sirotenko)
    public int Version { get; init; }
    public string Branch { get; init; }
    public bool IsInitializing { get; init; }
    public string Revision { get; init; }
    public string GitPath { get; init; }
    public string FilesPath { get; init; }
    public string TempPath { get; init; }
    public string SessionsPath { get; init; }
}