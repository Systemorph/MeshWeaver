#nullable enable
using System.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Reflection
{
    /// <summary>
    /// The <see cref="MemberInfoExtensions"/> class provides extensions methods on <see cref="MemberInfo"/>s to facilitate
    /// common reflection tasks 
    /// </summary>
    public static class MemberInfoExtensions
    {
        // 🚨 No static attribute caches. They were keyed by (attributeType, MemberInfo),
        // and a MemberInfo of a dynamically-compiled NodeType property keeps its
        // DeclaringType — and thus that type's collectible AssemblyLoadContext — alive
        // for the whole process, leaking the ALC across meshes/tests (these run on
        // generated content-type properties via the property editor / data grid). The
        // CLR already caches custom-attribute metadata per member internally, so the
        // direct reflection calls below are cheap; dropping the managed cache removes the
        // pin without a measurable cost. See NoStaticState.md.

        /// <summary>
        /// Tests if on the <paramref name="member"/> (or an ancestor of the member) an attribute of type <typeparamref name="T"/> is applied
        /// </summary>
        /// <typeparam name="T">The type of the attribute to test</typeparam>
        /// <param name="member">The member (property, method, field,...) to test</param>
        /// <param name="inherit">Flag whether to search ancestors of the member</param>
        /// <returns>True if an attribute of type <typeparamref name="T"/> is applied to the member</returns>
        public static bool HasAttribute<T>(this MemberInfo member, bool inherit = true)
            where T : Attribute
        {
            if (member == null)
                throw new ArgumentNullException(nameof(member));

            return Attribute.IsDefined(member, typeof(T), inherit);
        }

        /// <summary>
        /// Tests if on the <paramref name="parameter"/> (or an ancestor of the member) an attribute of type <typeparamref name="T"/> is applied
        /// </summary>
        /// <typeparam name="T">The type of the attribute to test</typeparam>
        /// <param name="parameter">The parameter to test</param>
        /// <param name="inherit">Flag whether to search ancestors of the member</param>
        /// <returns>True if an attribute of type <typeparamref name="T"/> is applied to the member</returns>
        public static bool HasAttribute<T>(this ParameterInfo parameter, bool inherit = true)
            where T : Attribute
        {
            if (parameter == null)
                throw new ArgumentNullException(nameof(parameter));

            return parameter.IsDefined(typeof(T), inherit);
        }

        /// <summary>
        /// Gets all attributes of type <typeparamref name="T"/> applied to the <paramref name="member"/> (or an ancestor of the member).
        /// </summary>
        /// <typeparam name="T">The type of the attributes to retrieve.</typeparam>
        /// <param name="member">The member (property, method, field, ...) to inspect.</param>
        /// <param name="inherit">Flag whether to search ancestors of the member.</param>
        /// <returns>The list of applied attributes of type <typeparamref name="T"/>; empty if none are present.</returns>
        public static List<T> GetMultipleCustomAttributes<T>(this MemberInfo member, bool inherit = true)
            where T : Attribute
        {
            return member.GetCustomAttributes(typeof(T), inherit).Cast<T>().ToList();
        }

        /// <summary>
        /// Gets the attribute of type <typeparamref name="T"/> which is applied to the <paramref name="member"/> (or an ancestor of the member) or null if the assembly
        /// has no attribute of this type
        /// </summary>
        /// <typeparam name="T">The type of the attribute</typeparam>
        /// <param name="member">The member (property, method, field,...) to get the attribute from</param>
        /// <param name="inherit">Flag whether to search ancestors of the member</param>
        /// <returns>The applied attribute of type <typeparamref name="T"/> or null</returns>
        public static T? GetSingleCustomAttribute<T>(this MemberInfo member, bool inherit = true)
            where T : Attribute
        {
            return member.MemberType is MemberTypes.Event or MemberTypes.Property
                ? (T?)Attribute.GetCustomAttributes(member, inherit).SingleOrDefault(a => a is T)
                : (T?)member.GetCustomAttributes(typeof(T), inherit).SingleOrDefault();
        }

        /// <summary>
        /// Tests if the given <paramref name="property"/> overrides the property of a base class
        /// </summary>
        /// <param name="property">The property to test</param>
        /// <returns>True, if the property overrides a base class property</returns>
        public static bool IsOverride(this PropertyInfo property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));

            var accessors = property.GetAccessors(true);
            return accessors.Any(x => x != x.GetBaseDefinition());
        }

        /// <summary>
        /// Tests if the <paramref name="property"/> is virtual, i.e. can or must be overridden. This is the
        /// case if the property is declared in an interface, is declared as abstract or declared as virtual
        /// </summary>
        /// <param name="property">The property to test</param>
        /// <returns>True, if the property is virtual</returns>
        public static bool IsVirtual(this PropertyInfo property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));

            var accessors = property.GetAccessors(true);
            return accessors.Any(x => x.IsVirtual && !x.IsFinal);
        }

        /// <summary>
        /// Tests if the <paramref name="property"/> hides a declaration in a base class, i.e. is declared with the new modifier
        /// </summary>
        /// <param name="property">The property to test</param>
        /// <returns>True, if the property hides a base class declaration</returns>
        public static bool IsHiding(this PropertyInfo property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));

            var declaringType = property.DeclaringType;
            if (declaringType!.IsInterface)
                return declaringType.GetInterfaces().Any(it => IsHiding(property, it, declaringType));
            else
                return IsHiding(property, declaringType.BaseType, declaringType);
        }

        private static bool IsHiding(PropertyInfo property, Type? baseType, Type declaringType)
        {
            if (baseType == null)
                return false;

            var baseProperty = baseType.GetProperty(property.Name, property.PropertyType);
            if (baseProperty == null)
                return false;

            if (baseProperty.DeclaringType == declaringType)
                return false;

            var baseGetter = baseProperty.GetGetMethod();
            if (baseGetter == null)
                return false;

            var getter = property.GetGetMethod();
            if (getter == null)
                return false;

            var thisMethodDefinition = getter.GetBaseDefinition();
            var baseMethodDefinition = baseGetter.GetBaseDefinition();
            return baseMethodDefinition.DeclaringType != thisMethodDefinition.DeclaringType;
        }

        /// <summary>
        /// Gets for the <paramref name="source"/> property the list of original <see cref="PropertyInfo"/>s declared in an interface
        /// </summary>
        /// <param name="source">The source property to inspect</param>
        /// <returns>A list of PropertyInfos declared in a interface</returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="source"/> is null</exception>
        /// <example>
        /// This example shows the usage of the method:
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\MemberExtensionsSnippets.cs" language="cs" region="GetPropertyDeclarationsFromInterfacesSample" />
        /// </example>
        /// <remarks>
        /// If the <paramref name="source"/> property has is already declared in an interface the method return an empty list
        /// </remarks>
        public static IEnumerable<PropertyInfo> GetPropertyDeclarationsFromInterfaces(this PropertyInfo source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (source.ReflectedType!.IsInterface)
                yield break;

            Type[] interfaces = source.ReflectedType.GetInterfaces();
            if (interfaces.Length == 0)
                yield break;

            MethodInfo? propertyGetter = source.GetGetMethod();
            if (propertyGetter == null)
                yield break;

            foreach (Type @interface in interfaces)
            {
                InterfaceMapping memberMap = source.ReflectedType!.GetInterfaceMap(@interface);
                PropertyInfo[] interfaceProperties = @interface.GetProperties();
                for (int i = 0; i < memberMap.TargetMethods.Length; i++)
                {
                    if (memberMap.TargetMethods[i] == propertyGetter)
                        yield return interfaceProperties.Single(pi => pi.GetGetMethod() == memberMap.InterfaceMethods[i]);
                }
            }
        }

        /// <summary>
        /// Gets for the <paramref name="implementationMethod"/> method the list of original <see cref="MethodInfo"/>s declared in an interface
        /// </summary>
        /// <param name="implementationMethod">The source method to inspect</param>
        /// <returns>A list of MethodInfo declared in a interface</returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="implementationMethod"/> is null</exception>
        /// <example>
        /// For an example see <see cref="GetPropertyDeclarationsFromInterfaces"/>
        /// </example>
        public static IEnumerable<MethodInfo> GetMethodsFromInterfaces(this MethodInfo implementationMethod)
        {
            // No static memoization cache: the key was a MethodInfo, which keeps its
            // DeclaringType (and that type's collectible ALC) alive process-wide. The
            // interface-map walk below is cheap and rarely hit. See NoStaticState.md.
            if (implementationMethod == null)
                throw new ArgumentNullException(nameof(implementationMethod));

            var reflectedType = implementationMethod.ReflectedType;
            if (reflectedType!.IsInterface)
                return new List<MethodInfo>();

            return GetMethodsFromInterfacesInner(implementationMethod);
        }

        private static List<MethodInfo> GetMethodsFromInterfacesInner(MethodInfo implMethod)
        {
            var reflectedType = implMethod.ReflectedType;
            Type[] interfaces = reflectedType!.GetInterfaces();
            if (interfaces.Length == 0)
                return new List<MethodInfo>();

            var methods = new List<MethodInfo>();

            foreach (var @interface in interfaces)
            {
                var interfaceMap = reflectedType.GetInterfaceMap(@interface);
                var interfaceMethods = @interface.GetMethods();

                for (var i = 0; i < interfaceMap.TargetMethods.Length; i++)
                {
                    if (interfaceMap.TargetMethods[i] == implMethod)
                        methods.Add(interfaceMethods.Single(m => m == interfaceMap.InterfaceMethods[i]));
                }
            }

            return methods;
        }
    }
}
