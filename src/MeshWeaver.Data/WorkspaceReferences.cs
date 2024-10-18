namespace MeshWeaver.Data;

public record WorkspaceStoreReference : WorkspaceReference<EntityStore>
{
    public string Path => "$";

    public override string ToString() => Path;
}

