namespace OpenSmc.Conventions
{
    public class ConventionService<T, TArg, TConvention> : IConventionService<T, TArg>
        where TConvention : ConventionService<T, TArg, TConvention>, new()
    {
        private readonly List<IConventionModifier<T, TArg>> addModifiers = new();
        private readonly List<IConventionModifier<T, TArg>> removeModifiers = new();
        private readonly List<IConventionModifier<T, TArg>> basicOrderModifiers = new();
        private readonly List<IConventionModifier<T, TArg>> reorderingModifiers = new(); 

        public static TConvention Instance { get; } = new();

        public IEnumerable<T> GetElements(IEnumerable<T> elements, TArg arg)
        {
            return GetElements(elements, x => x, arg);
        }

        public IEnumerable<TElement> GetElements<TElement>(IEnumerable<TElement> elements, Func<TElement, T> keySelector, TArg arg)
        {
            var ret = new OrderedElements<TElement,T>(elements, keySelector);
            addModifiers.ForEach(m => m.Modify(ret, arg));
            removeModifiers.ForEach(m => m.Modify(ret, arg));
            basicOrderModifiers.ForEach(m => m.Modify(ret, arg));
            var reordered = true;
            while (reordered)
            {
                reordered = reorderingModifiers.Any(x => x.Modify(ret, arg));
            }
            return ret.Elements;
        }

        public IConventionBuilder<T, TArg> Element(T element)
        {
            return new ConventionBuilder(this, element);
        }

        private void AddBasicOrderModifier(IConventionModifier<T, TArg> modifier)
        {
            basicOrderModifiers.Add(modifier);
        }

        private void AddAddModifier(IConventionModifier<T, TArg> modifier)
        {
            addModifiers.Add(modifier);
        }

        private void AddRemoveModifier(IConventionModifier<T, TArg> modifier)
        {
            removeModifiers.Add(modifier);
        }

        private void AddReorderingModifier(IConventionModifier<T, TArg> modifier)
        {
            reorderingModifiers.Add(modifier);
        }

        private class ConventionBuilder : IConventionBuilder<T, TArg>
        {
            private readonly T el;
            private IConditionModifier conditionModifier;
            private readonly ConventionService<T, TArg, TConvention> service;

            public ConventionBuilder(ConventionService<T, TArg, TConvention> service, T el)
            {
                this.service = service;
                this.el = el;
            }

            public void AtBeginning()
            {
                var modifier = Wrap(new AtBeginningConventionModifier<T, TArg>(el));
                service.AddBasicOrderModifier(modifier);
            }

            public void AtEnd()
            {
                var modifier = Wrap(new AtEndConventionModifier<T, TArg>(el));
                service.AddBasicOrderModifier(modifier);
            }

            public void Eliminates(T el2)
            {
                var modifier = Wrap(new RemoveConventionModifier<T, TArg>(el, el2));
                service.AddRemoveModifier(modifier);
            }

            public void DependsOn(T el2)
            {
                var modifier = Wrap(new ReorderConventionModifier<T, TArg>(el2, el));
                service.AddReorderingModifier(modifier);
            }

            public void ReplaceWith(T newEl)
            {
                var modifier = Wrap(new ReplaceConventionModifier<T, TArg>(el, newEl));
                service.AddAddModifier(modifier);
            }

            public void Delete()
            {
                var modifier = Wrap(new DeleteConventionModifier<T, TArg>(el));
                service.AddRemoveModifier(modifier);
            }

            public IConditionBuilder<T, TArg> Condition()
            {
                return new ConditionBuilder(this);
            }

            private IConventionModifier<T, TArg> Wrap(IConventionModifier<T, TArg> modifier)
            {
                var currentConditionalWrapper = conditionModifier;
                while (currentConditionalWrapper != null)
                {
                    var innerConditionWrapper = currentConditionalWrapper.InnerModifier as IConditionModifier;
                    if (innerConditionWrapper == null)
                        break;
                    currentConditionalWrapper = innerConditionWrapper;
                }

                if (currentConditionalWrapper == null)
                    return modifier;
                currentConditionalWrapper.InnerModifier = modifier;
                return conditionModifier;
            }

            internal void SetConditionWrapper(IConditionModifier newConditionModifier)
            {
                conditionModifier = (IConditionModifier)Wrap(newConditionModifier);
            }
        }

        private class ConditionBuilder : IConditionBuilder<T, TArg>
        {
            private readonly ConventionBuilder conventionBuilder;

            public ConditionBuilder(ConventionBuilder conventionBuilder)
            {
                this.conventionBuilder = conventionBuilder;
            }

            public IConventionBuilder<T, TArg> IsPresent(T el1)
            {
                var conditionModifier = new ContainsConditionModifier(el1);
                conventionBuilder.SetConditionWrapper(conditionModifier);
                return conventionBuilder;
            }

            public IConventionBuilder<T, TArg> IsTrue(Func<TArg, bool> func)
            {
                var conditionModifier = new FuncConditionModifier(func);
                conventionBuilder.SetConditionWrapper(conditionModifier);
                return conventionBuilder;
            }

            public IConventionBuilder<T, TArg> IsFalse(Func<TArg, bool> func)
            {
                var conditionModifier = new FuncConditionModifier((a)=>!func(a));
                conventionBuilder.SetConditionWrapper(conditionModifier);
                return conventionBuilder;
            }
        }

        private interface IConditionModifier : IConventionModifier<T, TArg>
        {
            IConventionModifier<T, TArg> InnerModifier { get; set; }
        }

        private class ContainsConditionModifier: IConditionModifier
        {
            public IConventionModifier<T, TArg> InnerModifier { get; set; }

            private readonly T el;

            public ContainsConditionModifier(T el)
            {
                this.el = el;
            }

            public bool Modify<TElement>(OrderedElements<TElement, T> collection, TArg arg)
            {
                if (collection.Contains(el))
                    return InnerModifier.Modify(collection, arg);
                return false;
            }

            public override string ToString()
            {
                return $"{InnerModifier} * with Contains {el} condition.";
            }
        }

        private class FuncConditionModifier : IConditionModifier
        {
            public IConventionModifier<T, TArg> InnerModifier { get; set; }

            private readonly Func<TArg, bool> func;

            public FuncConditionModifier(Func<TArg, bool> func)
            {
                this.func = func;
            }

            public bool Modify<TElement>(OrderedElements<TElement, T> collection, TArg arg)
            {
                if (func(arg))
                    return InnerModifier.Modify(collection, arg);
                return false;
            }

            public override string ToString()
            {
                return $"{InnerModifier} * with predicate";
            }
        }
    }
}