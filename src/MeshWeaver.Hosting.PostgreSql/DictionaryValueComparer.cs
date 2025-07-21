using System.Collections;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MeshWeaver.Hosting.PostgreSql
{
    /// <summary>
    /// Provides value comparison for dictionaries containing complex nested objects.
    /// Used by Entity Framework Core for value comparisons of JSON column values.
    /// </summary>
    public class DictionaryValueComparer() : ValueComparer<IReadOnlyDictionary<string, object>>(
        (d1, d2) => DictionaryEquals(d1!, d2!),
        d => GetDictionaryHashCode(d!),
        d => d)
    {
        private static bool DictionaryEquals(IReadOnlyDictionary<string, object> left, IReadOnlyDictionary<string, object> right)
        {
            if (left == null && right == null)
                return true;

            if (left == null || right == null)
                return false;

            if (left.Count != right.Count)
                return false;

            foreach (var kvp in left)
            {
                if (!right.TryGetValue(kvp.Key, out var rightObject))
                    return false;

                if (!ValueEquals(kvp.Value, rightObject))
                    return false;
            }

            return true;
        }

        private static bool ValueEquals(object left, object right)
        {
            if (left == null && right == null)
                return true;

            if (left == null || right == null)
                return false;

            // Handle nested dictionaries
            if (left is IReadOnlyDictionary<string, object> leftDict && right is IReadOnlyDictionary<string, object> rightDict)
                return DictionaryEquals(leftDict, rightDict);


            // Handle collections
            if (left is IEnumerable leftCollection && right is IEnumerable rightCollection &&
                !(left is string) && !(right is string))
                return CollectionEquals(leftCollection, rightCollection);

            // Try JsonSerializer for complex objects (more reliable deep comparison)
            if (left.GetType() == right.GetType() && !left.GetType().IsPrimitive)
            {
                try
                {
                    var leftJson = JsonSerializer.Serialize(left);
                    var rightJson = JsonSerializer.Serialize(right);
                    return leftJson == rightJson;
                }
                catch
                {
                    // Fall back to default equality if JSON serialization fails
                }
            }

            return EqualityComparer<object>.Default.Equals(left, right);
        }

        private static bool DictionaryEqualsNonGeneric(IReadOnlyDictionary<string, object> left, IReadOnlyDictionary<string,object> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (var entry in left)
            {
                if (!right.TryGetValue(entry.Key, out var rightObject))
                    return false;

                if (!ObjectEquals(entry.Value, rightObject))
                    return false;
            }

            return true;
        }

        private static bool CollectionEquals(IEnumerable left, IEnumerable right)
        {
            var leftList = left.Cast<object>().ToList();
            var rightList = right.Cast<object>().ToList();

            if (leftList.Count != rightList.Count)
                return false;

            // Compare each element
            for (int i = 0; i < leftList.Count; i++)
            {
                if (!ObjectEquals(leftList[i], rightList[i]))
                    return false;
            }

            return true;
        }

        private static bool ObjectEquals(object left, object right)
        {
            if (left == null && right == null)
                return true;

            if (left == null || right == null)
                return false;

            // Handle nested dictionaries
            if (left is IReadOnlyDictionary<string,object> leftDict && right is IReadOnlyDictionary<string, object> rightDict)
                return DictionaryEqualsNonGeneric(leftDict, rightDict);

            // Handle collections
            if (left is IEnumerable leftCollection && right is IEnumerable rightCollection &&
                !(left is string) && !(right is string))
                return CollectionEquals(leftCollection, rightCollection);

            // Try JsonSerializer for complex objects
            if (left.GetType() == right.GetType() && !left.GetType().IsPrimitive)
            {
                try
                {
                    var leftJson = JsonSerializer.Serialize(left);
                    var rightJson = JsonSerializer.Serialize(right);
                    return leftJson == rightJson;
                }
                catch
                {
                    // Fall back to default equality if JSON serialization fails
                }
            }

            return object.Equals(left, right);
        }

        private static int GetDictionaryHashCode(IReadOnlyDictionary<string, object> dictionary)
        {
            if (dictionary == null)
                return 0;

            int hashCode = 0;

            foreach (var kvp in dictionary.OrderBy(x => x.Key?.ToString()))
            {
                int keyHash = kvp.Key?.GetHashCode() ?? 0;
                int valueHash = GeobjectHashCode(kvp.Value);

                hashCode = HashCode.Combine(hashCode, keyHash, valueHash);
            }

            return hashCode;
        }

        private static int GeobjectHashCode(object value)
        {
            if (value == null)
                return 0;

            // Handle nested dictionaries
            if (value is IReadOnlyDictionary<string, object> dict)
                return GetDictionaryHashCode(dict);

            // Handle collections
            if (value is IEnumerable collection && !(value is string))
            {
                int collectionHash = 0;
                foreach (var item in collection)
                {
                    collectionHash = HashCode.Combine(collectionHash, item?.GetHashCode() ?? 0);
                }
                return collectionHash;
            }

            return value.GetHashCode();
        }

    }
}
