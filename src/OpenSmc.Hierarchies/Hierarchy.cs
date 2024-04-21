using AngleSharp.Common;
using AngleSharp.Dom;
using OpenSmc.Collections;
using OpenSmc.Data;
using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Hierarchies;

public class Hierarchy<T> : IHierarchy<T>
    where T : class, IHierarchicalDimension
{
    private IReadOnlyDictionary<object, object> elementsById;
    private Dictionary<object, HierarchyNode<T>> hierarchy;

    public Hierarchy(IReadOnlyDictionary<object, object> elementsById)
    {
        this.elementsById = elementsById;
        hierarchy = elementsById
            .Select(kvp => new KeyValuePair<object, T>(kvp.Key, (T)kvp.Value))
            .Select(dim => new HierarchyNode<T>(
                dim.Key,
                (T)dim.Value,
                dim.Value.Parent,
                (T)elementsById[dim.Value.Parent]
            ))
            .ToDictionary(x => x.Id);

        AddLevels();
    }

    private void AddLevels()
    {
        foreach (var id in hierarchy.Values.Select(x => x.Id).ToArray())
            GetLevel(id);
    }

    private int GetLevel(object id)
    {
        var node = hierarchy[id];
        if (node.ParentId == null || node.ParentId.Equals(string.Empty) || node.Level > 0)
            return node.Level;

        return (hierarchy[node.Id] = node with { Level = GetLevel(node.ParentId) + 1 }).Level;
    }

    public T Get(object id)
    {
        if (id == null || !elementsById.TryGetValue(id, out var ret))
            return null;
        return (T)ret;
    }

    public HierarchyNode<T> GetHierarchyNode(object id)
    {
        return hierarchy.GetValueOrDefault(id);
    }

    public T[] Children(object id)
    {
        var targetKey = id ?? "<null>";
        return elementsById.Values.Cast<T>().Where(x => x.Parent == id).ToArray();
    }

    // public T[] Descendants(object id, bool includeSelf = false)
    // {
    //     id ??= "";

    //     if (includeSelf)
    //         elementsBySystemNameAndLevels
    //             .Where(x => x.Value.Values.Contains(id))
    //             .Select(x => elementsById[x.Key])
    //             .ToArray();

    //     return elementsBySystemNameAndLevels
    //                 .Where(x => !x.Key.Equals(id) && x.Value.Values.Contains(id))
    //                 .Select(x => elementsById[x.Key])
    //                 .ToArray();
    // }

    // public T[] Ancestors(object id, bool includeSelf = false)
    // {
    //     id ??= "";

    //     if (!elementsBySystemNameAndLevels.TryGetValue(id, out var levels))
    //         return default;

    //     if (includeSelf)
    //         return levels.Select(x => elementsById[x.Value]).Cast<T>().ToArray();

    //     return levels
    //         .Where(x => x.Value != id)
    //         .Select(x => elementsById[x.Value])
    //         .Cast<T>()
    //         .ToArray();
    // }

    // public T[] Siblings(object id, bool includeSelf = false)
    // {
    //     id ??= "";

    //     if (includeSelf)
    //         elementsById
    //             .Values.Cast<T>()
    //             .Where(x => x.Parent == Get(id).Parent)
    //             .ToArray();

    //     return elementsById
    //         .Values.Cast<T>()
    //         .Where(x => x.Parent == Get(id).Parent && x.id != id)
    //         .ToArray();
    // }

    // public int Level(object id)
    // {
    //     if (!hierarchy.TryGetValue(id, out var levels))
    //         return 0;
    //     return levels.Keys.Max();
    // }

    // public T AncestorAtLevel(object id, int level)
    // {
    //     if (!elementsBySystemNameAndLevels.TryGetValue(id, out var levels))
    //         return null;
    //     if (!levels.TryGetValue(level, out var dimName))
    //         return null;
    //     elementsById.TryGetValue(dimName, out var dim);
    //     return dim;
    // }

    // public T[] DescendantsAtLevel(object id, int level)
    // {
    //     id ??= "";

    //     return elementsBySystemNameAndLevels
    //         .Where(x => x.Value.Values.Contains(id) && x.Value.Keys.Max() == level)
    //         .Select(x => elementsById[x.Key])
    //         .Cast<T>()
    //         .ToArray();
    // }

    // private record HierarchyNode(object ChildId, T Child, object ParentId, T Parent)
    // {
    //     public int Level { get; internal set; }
    // }

    // private void AddChildren(int level, IEnumerable<HierarchyNode> pairs)
    // {
    //     var dimensionsFormPreviousLevel =
    //         level == 0 ? new List<string>() : dimensionsByLevel[level - 1];

    //     var dimensionsByThisLevel = pairs
    //         .GroupBy(x =>
    //             level == 0 ? x.Parent == null : dimensionsFormPreviousLevel.Contains(x.ParentId)
    //         )
    //         .ToDictionary(x => x.Key, y => y);

    //     if (dimensionsByThisLevel.TryGetValue(true, out var dimensionsOnThisLevel))
    //     {
    //         foreach (var dim in dimensionsOnThisLevel)
    //         {
    //             elementsBySystemNameAndLevels[dim.ChildId] = new Dictionary<int, string>
    //             {
    //                 { level, dim.ChildId }
    //             };

    //             if (dim.Parent != null)
    //             {
    //                 foreach (var element in elementsBySystemNameAndLevels[dim.ParentId])
    //                     elementsBySystemNameAndLevels[dim.ChildId].Add(element.Key, element.Value);
    //             }
    //         }

    //         dimensionsByLevel[level] = dimensionsOnThisLevel
    //             .Select(x => x.Child.id)
    //             .ToList();
    //     }

    //     if (dimensionsByThisLevel.TryGetValue(false, out var otherDimensions))
    //         AddChildren(level + 1, otherDimensions);
    // }
}
