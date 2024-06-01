namespace OpenSmc.Layout;

public class SortByTypeRelevanceRegistry<T>
{
    private readonly List<(Type Type, T Item)> items = new();

    public void Register(Type type, T item)
    {
        items.Insert(0, (type, item));
    }
    public T Get(Type type)
    {
        var typeFilteredFactories = items.Where(x => x.Type != null && IsRelevantType(x.Type, type))
                                         .Select((x, i) => (Index: i, Type: x.Type, Item: x.Item))
                                         .OrderBy(x => x, new SortByRelevanceAndOrder())
                                         .Select(x => x.Item);

        return typeFilteredFactories.FirstOrDefault();
    }

    public class SortByRelevanceAndOrder : IComparer<(int Index, Type Type, T Item)>
    {
        public int Compare((int Index, Type Type, T Item) inp1, (int Index, Type Type, T Item) inp2)
        {
            var type1 = inp1.Type;
            var type2 = inp2.Type;

            var isType1RelevantForType2 = IsRelevantType(type1, type2);

            if (isType1RelevantForType2 && IsRelevantType(type2, type1))
            {
                var index1 = inp1.Index;
                var index2 = inp2.Index;
                return Comparer<int>.Default.Compare(index1, index2);
            }

            if (isType1RelevantForType2)
            {
                return 1;
            }

            return -1;
        }
    }

    public static bool IsRelevantType(Type type, Type actualType)
    {
        if (!type.IsGenericTypeDefinition)
        {
            return type.IsAssignableFrom(actualType);
        }

        var baseChain = actualType;

        while (baseChain is not null)
        {
            if (baseChain.IsGenericType && baseChain.GetGenericTypeDefinition() == type)
            {
                return true;
            }

            baseChain = baseChain.BaseType;
        }

        foreach (var i in actualType.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == type)
            {
                return true;
            }
        }

        return false;
    }

}
