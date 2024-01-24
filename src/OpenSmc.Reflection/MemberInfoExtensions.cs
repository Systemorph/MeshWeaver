using System.Reflection;

namespace OpenSmc.Reflection
{
    /// <summary>
    /// The <see cref="MemberInfoExtensions"/> class provides extensions methods on <see cref="MemberInfo"/>s to facilitate
    /// common reflection tasks 
    /// </summary>
    public static class MemberInfoExtensions
    {
        private static readonly CreatableObjectStore<Type, ICustomAttributeProvider, bool, bool> HasAttributeCache = new CreatableObjectStore<Type, ICustomAttributeProvider, bool, bool>(HasAttributeCacheFactory);

        private static bool HasAttributeCacheFactory(Type attributeType, ICustomAttributeProvider attributeProvider, bool inherit)
        {
            var memberInfo = attributeProvider as MemberInfo;
            return memberInfo != null
                       ? Attribute.IsDefined(memberInfo, attributeType, inherit)
                       : attributeProvider.IsDefined(attributeType, inherit);
        }

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

            return HasAttributeCache.GetInstance(typeof(T), member, inherit);
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

            return HasAttributeCache.GetInstance(typeof(T), parameter, inherit);
        }

        private static readonly CreatableObjectStore<Type, MemberInfo, bool, Attribute> SingleCustomAttributeCache = new CreatableObjectStore<Type, MemberInfo, bool, Attribute>(SingleCustomAttributeCacheFactory);

        private static Attribute SingleCustomAttributeCacheFactory(Type type, MemberInfo member, bool inherit)
        {
            switch (member.MemberType)
            {
                case MemberTypes.Event:
                case MemberTypes.Property:
                    {
                        var attributes = Attribute.GetCustomAttributes(member, inherit);
                        var attribute = attributes.SingleOrDefault(type.IsInstanceOfType);
                        return attribute;
                    }
                default:
                    {
                        var attributes = member.GetCustomAttributes(type, inherit);
                        return (Attribute)attributes.SingleOrDefault();
                    }
            }
        }

        private static readonly CreatableObjectStore<Type, MemberInfo, bool, List<Attribute>> MultipleCustomAttributeCache = new CreatableObjectStore<Type, MemberInfo, bool, List<Attribute>>(MultipleCustomAtrbuteCacheFactory);

        private static List<Attribute> MultipleCustomAtrbuteCacheFactory(Type type, MemberInfo member, bool inherit)
        {
            return member.GetCustomAttributes(type, inherit).Cast<Attribute>().ToList();
        }

        public static List<T> GetMultipleCustomAttributes<T>(this MemberInfo member, bool inherit = true)
            where T : Attribute
        {
            return MultipleCustomAttributeCache.GetInstance(typeof(T), member, inherit).Cast<T>().ToList();
        }

        /// <summary>
        /// Gets the attribute of type <typeparamref name="T"/> which is applied to the <paramref name="member"/> (or an ancestor of the member) or null if the assembly
        /// has no attribute of this type
        /// </summary>
        /// <typeparam name="T">The type of the attribute</typeparam>
        /// <param name="member">The member (property, method, field,...) to get the attribute from</param>
        /// <param name="inherit">Flag whether to search ancestors of the member</param>
        /// <returns>The applied attribute of type <typeparamref name="T"/> or null</returns>
        public static T GetSingleCustomAttribute<T>(this MemberInfo member, bool inherit = true)
            where T : Attribute
        {
            return (T)SingleCustomAttributeCache.GetInstance(typeof(T), member, inherit);
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

        private static bool IsHiding(PropertyInfo property, Type baseType, Type declaringType)
        {
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

            MethodInfo propertyGetter = source.GetGetMethod();
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

        private static readonly CreatableObjectStore<MethodInfo, List<MethodInfo>> MethodsFromInterfacesCache = new CreatableObjectStore<MethodInfo, List<MethodInfo>>(GetMethodsFromInterfacesInner);

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
            if (implementationMethod == null)
                throw new ArgumentNullException(nameof(implementationMethod));

            var reflectedType = implementationMethod.ReflectedType;
            if (reflectedType!.IsInterface)
                return new List<MethodInfo>();

            return MethodsFromInterfacesCache.GetInstance(implementationMethod);
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