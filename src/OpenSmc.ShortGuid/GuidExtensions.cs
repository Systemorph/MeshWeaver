namespace OpenSmc.ShortGuid;

public static class GuidExtensions
{
    public static Guid AsGuid(this string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.Empty;
        return new CSharpVitamins.ShortGuid(id);
    }

    public static string AsString(this Guid guid)
    {
        if (guid == Guid.Empty)
            return null;

        return new CSharpVitamins.ShortGuid(guid).ToString();
    }
}
