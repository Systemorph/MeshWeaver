namespace MeshWeaver.Collections
{
    public static class NullableSequenceEqualsExtensions
    {
        public static bool NullableSequenceEquals<T>(this IEnumerable<T> s1, IEnumerable<T> s2)
            where T : class
        {
            if (s1 != null && s2 != null)
                return s1.SequenceEqual(s2);
            return s1 == null && s2 == null;
        }
    }
}
