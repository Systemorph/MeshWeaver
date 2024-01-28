namespace OpenSmc.Conventions
{
    public class OrderedElements<T, TKey>
    {
        private readonly LinkedList<KeyValuePair<TKey, T>> elements;
        private readonly HashSet<TKey> hash;

        public OrderedElements(IEnumerable<T> elements, Func<T, TKey> keySelector)
        {
            this.elements = new LinkedList<KeyValuePair<TKey, T>>(elements.Select(x => new KeyValuePair<TKey, T>(keySelector(x), x)));
            hash = new HashSet<TKey>(this.elements.Select(x => x.Key));
        }

        public bool Contains(TKey el)
        {
            return hash.Contains(el);
        }

        public IEnumerable<T> Elements => elements.Select(x => x.Value);

        public bool PutInOrder(TKey el1, TKey el2)
        {
            var el2Indexes = GetAllIndexes(el2);
            if (!el2Indexes.Any())
                return false;
            var el2Node = el2Indexes[0];

            var indexesToMove = GetAllIndexes(el1).Where(x => x.index > el2Node.index).ToArray();
            if (!indexesToMove.Any())
                return false;
            foreach (var x in indexesToMove)
            {
                elements.Remove(x.item);
                elements.AddBefore(el2Node.item, x.item);
            }

            return true;
        }

        private List<(int index, LinkedListNode<KeyValuePair<TKey, T>> item)> GetAllIndexes(TKey el)
        {
            var res = new List<(int, LinkedListNode<KeyValuePair<TKey, T>>)>();

            int i = 0;
            for (var it = elements.First; it != null; it = it.Next)
            {
                if (it.Value.Key.Equals(el))
                    res.Add((i, it));
                i++;
            }
            return res;
        }

        public bool PutAtBeginning(TKey el)
        {
            if (elements.Count == 0)
                return false;

            var indexesToMove = GetAllIndexes(el).Where((x, i) => x.index > i).ToList();
            if (!indexesToMove.Any())
                return false;

            indexesToMove.Reverse();
            foreach (var x in indexesToMove)
            {
                elements.Remove(x.item);
                elements.AddFirst(x.item);
            }
            return true;
        }

        public bool PutAtEnd(TKey el)
        {
            var elementsCount = elements.Count;
            if (elementsCount == 0)
                return false;

            var indexesToMove = GetAllIndexes(el);
            indexesToMove.Reverse();

            indexesToMove = indexesToMove.Where((x, i) => x.index < elementsCount - 1 - i).ToList();
            if (!indexesToMove.Any())
                return false;

            foreach (var x in indexesToMove)
            {
                elements.Remove(x.item);
                elements.AddLast(x.item);
            }
            return true;
        }

        public bool Remove(TKey el)
        {
            var found = hash.Remove(el);
            if (found)
            {
                for (var it = elements.First; it != null;)
                {
                    var next = it.Next;
                    if (it.Value.Key.Equals(el))
                        elements.Remove(it); // as a side effect it.Next == null

                    it = next;
                }
            }
            return found;
        }

        public bool Replace(TKey oldKey, TKey newKey)
        {
            if (!typeof(T).IsAssignableFrom(typeof(TKey)))
                throw new ArgumentException($"");

            var indexes = GetAllIndexes(oldKey);
            if (!indexes.Any())
                return false;

            foreach (var x in indexes)
                x.item.Value = new KeyValuePair<TKey, T>(newKey, (T)(object)newKey);

            hash.Remove(oldKey);
            hash.Add(newKey);
            return true;
        }
    }
}