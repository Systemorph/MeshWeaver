namespace OpenSmc.Reflection;

public static class TypeExtensions
{

    /// <summary>
    /// Tests if the given <paramref name="type"/> is a static class
    /// </summary>
    /// <param name="type">The type to test</param>
    /// <returns>True, if the given <paramref name="type"/> is a static class.</returns>
    /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
    public static bool IsStatic(this Type type)
    {
        if (type == null)
            throw new ArgumentNullException(nameof(type));

        return type.IsAbstract && type.IsSealed;
    }
}
