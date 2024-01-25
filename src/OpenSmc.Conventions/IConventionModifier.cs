namespace OpenSmc.Conventions
{
    public interface IConventionModifier<T, in TArg>
    {
        bool Modify<TElement>(OrderedElements<TElement, T> collection, TArg arg);
    }

    public class AtBeginningConventionModifier<T, TArg> : IConventionModifier<T, TArg>
    {
        private readonly T el;

        public AtBeginningConventionModifier(T el)
        {
            this.el = el;
        }

        public bool Modify<TElement>(OrderedElements<TElement, T> collection, TArg arg)
        {
            return collection.PutAtBeginning(el);
        }

        public override string ToString()
        {
            return $"AtBeginning: {{{el}}}";
        }
    }

    public class AtEndConventionModifier<T, TArg> : IConventionModifier<T, TArg>
    {
        private readonly T el;

        public AtEndConventionModifier(T el)
        {
            this.el = el;
        }

        public bool Modify<TElement>(OrderedElements<TElement, T> collection, TArg arg)
        {
            return collection.PutAtEnd(el);
        }

        public override string ToString()
        {
            return $"AtEnd: {{{el}}}";
        }
    }

    public class ReorderConventionModifier<T, TArg> : IConventionModifier<T, TArg>
    {
        private readonly T el1;
        private readonly T el2;

        public ReorderConventionModifier(T el1, T el2)
        {
            this.el1 = el1;
            this.el2 = el2;
        }

        public bool Modify<TElement>(OrderedElements<TElement, T> collection, TArg arg)
        {
            return collection.PutInOrder(el1, el2);
        }

        public override string ToString()
        {
            return $"{{{el2}}} dependsOn {{{el1}}} ";
        }
    }

    public class RemoveConventionModifier<T, TArg> : IConventionModifier<T, TArg>
    {
        private readonly T el1;
        private readonly T el2;

        public RemoveConventionModifier(T el1, T el2)
        {
            this.el1 = el1;
            this.el2 = el2;
        }

        public bool Modify<TElement>(OrderedElements<TElement,T> collection, TArg arg)
        {
            if (!collection.Contains(el1))
                return false;
            return collection.Remove(el2);
        }

        public override string ToString()
        {
            return $"{{{el1}}} eliminates {{{el2}}} ";
        }
    }

    public class DeleteConventionModifier<T, TArg> : IConventionModifier<T, TArg>
    {
        private readonly T el;

        public DeleteConventionModifier(T el)
        {
            this.el = el;
        }

        public bool Modify<TElement>(OrderedElements<TElement,T> collection, TArg arg)
        {
            return collection.Remove(el);
        }

        public override string ToString()
        {
            return $"Delete: {{{el}}}";
        }
    }

    public class ReplaceConventionModifier<T, TArg> : IConventionModifier<T, TArg>
    {
        private readonly T oldKey;
        private readonly T newKey;

        public ReplaceConventionModifier(T oldKey, T newKey)
        {
            this.oldKey = oldKey;
            this.newKey = newKey;
        }

        public bool Modify<TElement>(OrderedElements<TElement, T> collection, TArg arg)
        {
            return collection.Replace(oldKey, newKey);
        }

        public override string ToString()
        {
            return $"{{{newKey}}} replaces {{{oldKey}}}";
        }
    }
}