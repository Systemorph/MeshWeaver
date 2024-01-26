namespace OpenSmc.ShortGuid;

public static class GuidExtensions
{
    public static string AsString(this Guid guid)
    {
        if (guid == Guid.Empty)
            return null;

        return new CSharpVitamins.ShortGuid(guid).ToString();
    }
}
