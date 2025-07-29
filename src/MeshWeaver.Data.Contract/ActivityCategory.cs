namespace MeshWeaver.Data;

public static class ActivityCategory
{
    public const string DataUpdate = nameof(DataUpdate);
    public const string Import = nameof(Import);
    public const string Unknown = nameof(Unknown);
}

public record ChangeActivityCategoryRequest(string Category);
