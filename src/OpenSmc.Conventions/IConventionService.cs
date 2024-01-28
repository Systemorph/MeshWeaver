using System.Collections;

namespace OpenSmc.Conventions
{
    public interface IConventionService<T, TArg>
    {
        IEnumerable<T> GetElements(IEnumerable<T> elements, TArg arg);
        IEnumerable<TElement> GetElements<TElement>(IEnumerable<TElement> elements, Func<TElement, T> keySelector, TArg arg);
        IConventionBuilder<T, TArg> Element(T element);
    }

    public static class ConventionServiceExtensions
    {
        /// <summary>
        /// Reorders and filters enumerable of conventions according to rules conventionService rules. Tailored to suit conventionServices with Type as TElement
        /// </summary>
        /// <typeparam name="TConvention"></typeparam>
        /// <typeparam name="TConditionArg"></typeparam>
        /// <param name="conventionService"></param>
        /// <param name="conventions">conventions to reoder. Note: if it has deferred enumeration, it will be forced once</param>
        /// <param name="conditionArg"></param>
        /// <returns>Reordered and filtered conventions enumerable. Note: it has deferred enumeration</returns>
        public static IEnumerable<TConvention> Reorder<TConvention, TConditionArg>(this IConventionService<Type, TConditionArg> conventionService, IEnumerable<TConvention> conventions, TConditionArg conditionArg)
        {
            return conventionService.Reorder(conventions, c => c.GetType(), conditionArg);
        }

        /// <summary>
        /// Reorders and filters enumerable of conventions according to rules conventionService rules
        /// </summary>
        /// <typeparam name="TConvention"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TConditionArg"></typeparam>
        /// <param name="conventionService"></param>
        /// <param name="conventions">conventions to reoder. Note: if it has deferred enumeration, it will be forced once</param>
        /// <param name="keySelector">selector convention => conventionService element</param>
        /// <param name="conditionArg"></param>
        /// <returns>Reordered and filtered conventions enumerable. Note: it has deferred enumeration</returns>
        public static IEnumerable<TConvention> Reorder<TConvention, TKey, TConditionArg>(this IConventionService<TKey, TConditionArg> conventionService, IEnumerable<TConvention> conventions, Func<TConvention,TKey> keySelector, TConditionArg conditionArg)
        {
            if (conventionService == null)
                throw new ArgumentNullException(nameof(conventionService));
            if (conventions == null)
                throw new ArgumentNullException(nameof(conventions));
            if (keySelector == null)
                throw new ArgumentNullException(nameof(keySelector));

            if (!(conventions is ICollection<TConvention>) && !(conventions is ICollection))
                conventions = conventions.ToArray();

            var filteredOrderedConventions = conventionService.GetElements(conventions, keySelector, conditionArg);
            return filteredOrderedConventions;
        }
    }

    public interface IConventionBuilder<in T, TArg>
    {
        void AtBeginning();
        void AtEnd();
        void Eliminates(T el2);
        void DependsOn(T el2);
        void ReplaceWith(T newEl);
        void Delete();
        IConditionBuilder<T, TArg> Condition();
    }

    public interface IConditionBuilder<in T, TArg>
    {
        IConventionBuilder<T, TArg> IsPresent(T el);
        IConventionBuilder<T, TArg> IsTrue(Func<TArg, bool> func);
        IConventionBuilder<T, TArg> IsFalse(Func<TArg, bool> func);        
    }

}