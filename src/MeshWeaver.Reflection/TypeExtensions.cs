using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MeshWeaver.Collections;

namespace MeshWeaver.Reflection
{
    /// <summary>
    /// The <see cref="TypeExtensions"/> class provides extensions methods on <see cref="Type"/> which simplify
    /// common tasks when working with Types
    /// </summary>
    public static class TypeExtensions
    {
        /// <summary>
        /// Tests if the type is an anonymous type
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if the type is anonymous</returns>
        /// <example>
        /// This example shows the usage of the method:
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\TypeExtensionsSnippets.cs" language="cs" region="IsAnonymousSample" />
        /// </example>
        /// <remarks>
        /// Idea taken from http://www.liensberger.it/web/blog/?p=191
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        public static bool IsAnonymous(this Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            // HACK: The only way to detect anonymous types right now.
            return Attribute.IsDefined(type, typeof(CompilerGeneratedAttribute), false)
                   && type.IsGenericType && type.Name.Contains("AnonymousType")
                   && (type.Name.StartsWith("<>") || type.Name.StartsWith("VB$"));
        }

        /// <summary>
        /// Tests if the type is a closure type
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if the type is a Closure type</returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        public static bool IsClosure(this Type type)
        {
            if (type == null) 
                throw new ArgumentNullException(nameof(type));

            var fullName = type.FullName;
            if (fullName == null)
                return false;

            // Expression.Compile - produced closure
            if (fullName == "System.Runtime.CompilerServices.Closure")
                return true;

            // Usual closure
            if (type.IsClass
                && type.IsNestedPrivate
                && type.IsSealed
                && fullName.Contains("+<>c__DisplayClass"))
                return true;

            return false;
        }

        /// <summary>
        /// Tests if the <paramref name="type"/> is of the form <see cref="Nullable{T}"/>
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if the <paramref name="type"/> is of the form <see cref="Nullable{T}"/>.</returns>
        /// <example>
        /// This example shows the usage of the method:
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\TypeExtensionsSnippets.cs" language="cs" region="IsNullableGenericSample" />
        /// </example>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        public static bool IsNullableGeneric(this Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            return
                    type.IsValueType &&
                    type.IsGenericType &&
                    type.GetGenericTypeDefinition() == typeof(Nullable<>);
        }

        /// <summary>
        /// Tests if a variable of the given <paramref name="type"/> has <see langword="null"/> as default value
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if variables of the given <paramref name="type"/> has <see langword="null"/> as default value.</returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        public static bool HasNullAsDefaultValue(this Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            return type.IsClass || type.IsInterface || type.IsNullableGeneric();
        }

        /// <summary>
        /// Tests if the given <paramref name="type"/> is a static class
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if the given <paramref name="type"/> is a static class.</returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        public static bool IsStatic(this Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            return type.IsAbstract && type.IsSealed;
        }

        /// <summary>
        /// Gets the inheritance hierarchy of the given <paramref name="type"/> from top to bottom.
        /// </summary>
        /// <param name="type">The type to get the base types from</param>
        /// <param name="includeSelf">Flag, defining if the <paramref name="type"/> itself should be included as first element</param>
        /// <returns>List of base types</returns>
        /// <example>
        /// This example shows the usage of the method:
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\TypeExtensionsSnippets.cs" language="cs" region="GetBaseTypesSample" />
        /// </example>
        /// <remarks>
        /// For details see also <see cref="Type.BaseType"/>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        public static IEnumerable<Type> GetBaseTypes(this Type type, bool includeSelf = false)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var start = includeSelf ? type : type.BaseType;
            for (var t = start; t != null; t = t.BaseType)
                yield return t;
        }

        /// <summary>
        /// Gets a user-friendly name of the type
        /// </summary>
        /// <param name="type">The type to get the name for</param>
        /// <param name="options">options flags affecting the behavior. <see cref="SpeakingNameOptions"/>> for details</param>
        /// <returns>A user friendly, speaking name of the type</returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        /// <example>
        /// This example shows the usage of the method:
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\TypeExtensionsSnippets.cs" language="cs" region="GetSpeakingNameSample" />
        /// </example>
        /// <remarks>
        /// Useful for generic types as standard Name and FullName of the type are garbled
        /// </remarks>
        public static string GetSpeakingName(this Type type, SpeakingNameOptions options = SpeakingNameOptions.Default)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var omitNamespaces = options.HasFlag(SpeakingNameOptions.OmitNamespaces);
            var omitTypeArgNames = options.HasFlag(SpeakingNameOptions.OmitTypeArgNames);

            string name;
            if (omitNamespaces)
            {
                name = type.Name;
            }
            else
            {
                name = type.FullName;
                // ReSharper disable ConditionIsAlwaysTrueOrFalse <-- Resharper is lying here. E.g. this is false for generic argument types
                if (!omitTypeArgNames && name == null)
                    name = type.Name;
                // ReSharper restore ConditionIsAlwaysTrueOrFalse
            }

            var subOptions = SpeakingNameOptions.Default;
            if (omitTypeArgNames)
                subOptions |= SpeakingNameOptions.OmitTypeArgNames;
            if (omitNamespaces)
                subOptions |= SpeakingNameOptions.OmitNamespaces;

            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                
                return string.Format("{0}[{1}]", GetSpeakingName(elementType, subOptions), new string(',', type.GetArrayRank() - 1));
            }

            if (!type.IsGenericType)
                return name;

            var sb = new StringBuilder(name!.Substring(0, name.IndexOf('`')));

            if (options.HasFlag(SpeakingNameOptions.OmitBrackets))
                return sb.ToString();

            sb.Append("<");

            var genericArgNames = type.GetGenericArguments().Select(x => GetSpeakingName(x, subOptions));
            using (var enumerator = genericArgNames.GetEnumerator())
            {
                if (enumerator.MoveNext())
                {
                    if (!omitTypeArgNames)
                        sb.Append(enumerator.Current);

                    while (enumerator.MoveNext())
                    {
                        sb.Append(",");
                        if (!omitTypeArgNames)
                            sb.Append(enumerator.Current);
                    }
                }
            }

            sb.Append(">");
            return sb.ToString();
        }

        /// <summary>
        /// Tests if the given <paramref name="type"/> is an Attribute (inherits from <see cref="Attribute"/>)
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if the type is an Attribute</returns>
        public static bool IsAttribute(this Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            return typeof(Attribute).IsAssignableFrom(type);
        }

        /// <summary>
        /// Tests if the given <paramref name="type"/> represents an integer value. This can
        /// be any of the signed or unsigned primitive types as well as their nullable variations
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if the type represents an integer</returns>
        public static bool IsIntegerType(this Type type)
        {
            return IntegerTypes.Contains(type) || IntegerTypes.Contains(Nullable.GetUnderlyingType(type));
        }

        public static bool IsNullableIntegerType(this Type type)
        {
            return IntegerTypes.Contains(Nullable.GetUnderlyingType(type));
        }

        /// <summary>
        /// Tests if the given <paramref name="type"/> represents a real number. This can
        /// be any of the primitive types which store real number.
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns>True, if the type represents a real number</returns>
        public static bool IsRealType(this Type type)
        {
            return RealTypes.Contains(type) || RealTypes.Contains(Nullable.GetUnderlyingType(type));
        }

        public static bool IsNullableRealType(this Type type)
        {
            return RealTypes.Contains(Nullable.GetUnderlyingType(type));
        }


        public static readonly List<Type> IntegerTypes = new List<Type>
                                                         {
                                                             typeof(Byte),
                                                             typeof(SByte),
                                                             typeof(UInt16),
                                                             typeof(UInt32),
                                                             typeof(UInt64),
                                                             typeof(Int16),
                                                             typeof(Int32),
                                                             typeof(Int64)
                                                         };

        public static readonly List<Type> RealTypes = new List<Type>
                                                          {
                                                                  typeof(Decimal),
                                                                  typeof(Double),
                                                                  typeof(Single)
                                                          };

        /// <inheritdoc cref="GetCustomAttributesInherited{T}(Type)" />
        /// <param name="type">The type to inspect</param>
        /// <param name="typeFilterFunc">A filter function which is applied on <paramref name="type"/> and its ancestor base types</param>
        public static IEnumerable<T> GetCustomAttributesInherited<T>(this Type type, Func<Type, bool> typeFilterFunc)
            where T : Attribute
        {
            return type.GetBaseTypes(true).TakeWhile(typeFilterFunc).SelectMany(t => t.GetCustomAttributes<T>(false));
        }

        /// <summary>
        /// Gets all custom attributes of which are from type <typeparamref name="T"/> or inherit from type <typeparamref name="T"/>
        /// which are applied to the given <paramref name="type"/> or one of its ancestor base types
        /// </summary>
        /// <typeparam name="T">The type of the custom attribute to get</typeparam>
        /// <param name="type">The type to inspect</param>
        /// <returns>List of custom attributes of type <typeparamref name="T"/></returns>
        public static IEnumerable<T> GetCustomAttributesInherited<T>(this Type type) where T : Attribute
        {
            return GetCustomAttributesInherited<T>(type, _ => true);
        }

        internal const string AttributeSuffix = "Attribute";

        /// <summary>
        /// Gets the short name (without the "Attribute" postfix) of an attribute of type <typeparamref name="T"/>
        /// </summary>
        /// <typeparam name="T">Type of the attribute</typeparam>
        /// <param name="includeNamespace">Flag defining if the Namespace should be included. Default is false</param>
        /// <returns>The short name of the attribute type</returns>
        /// <exception cref="ArgumentException">Thrown, if the type <typeparamref name="T"/> is not an attribute</exception>
        public static string GetShortAttributeName<T>(bool includeNamespace = false)
                where T : Attribute
        {
            return GetAttributeNameInner(typeof(T), includeNamespace);
        }

        /// <summary>
        /// Gets the shortname (without the "Attribute" postfix) of an attribute of type <paramref name="attributeType"/>
        /// </summary>
        /// <param name="attributeType">Type of the attribute</param>
        /// <param name="includeNamespace">Flag defining if the Namespace should be included. Default is false</param>
        /// <returns>The shortname of the attribute type</returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="attributeType"/> is <see langword="null"/></exception>
        /// <exception cref="ArgumentException">Thrown, if the <paramref name="attributeType"/> is not an attribute</exception>
        public static string GetShortAttributeName(this Type attributeType, bool includeNamespace = false)
        {
            if (attributeType == null)
                throw new ArgumentNullException(nameof(attributeType));
            if (!attributeType.IsAttribute())
                throw new ArgumentException(String.Format("Type '{0}' is not an attribute type", attributeType), nameof(attributeType));

            return GetAttributeNameInner(attributeType, includeNamespace);
        }

        private static string GetAttributeNameInner(Type attributeType, bool includeNamespace)
        {
            var name = includeNamespace
                               ? attributeType.FullName
                               : attributeType.Name;

            return name!.EndsWith(AttributeSuffix) ? name[..^AttributeSuffix.Length] : name;
        }

        /// <summary>
        /// Tests if the given <paramref name="type"/> has a public property with the given <paramref name="name"/>
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <param name="name">The name of the property</param>
        /// <returns>True, if the <paramref name="type"/> has a public property with the given <paramref name="name"/></returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        public static bool HasPropertyWithName(this Type type, string name)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var originalProperty = type.GetProperty(name);
            return originalProperty != null;
        }

        /// <summary>
        /// Gets the generic argument types from the given <paramref name="type"/> as declared in the <paramref name="genericBaseType"/>
        /// </summary>
        /// <param name="type">The type to get the generic argument types from</param>
        /// <param name="genericBaseType">A base type of <paramref name="type"/> in which the generic type arguments are declared</param>
        /// <returns>Array of generic type arguments</returns>
        /// <example>
        /// Given this generic class
        /// <code source = "..\..\Systemorph.Utils.Test\Reflection\TypeExtensionsTest.cs" language="cs" region="GetGenericArgumentTypesTestData" />
        /// the method returns all generic argument types as "seen" by the <paramref name="genericBaseType"/>
        /// <code source = "..\..\Systemorph.Utils.Test\Reflection\TypeExtensionsTest.cs" language="cs" region="GetGenericArgumentTypesSample" />
        /// </example>
        public static Type[] GetGenericArgumentTypes(this Type type, Type genericBaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == genericBaseType)
                return type.GetGenericArguments();

            var foundInterface = type.GetInterfaces()
                                     .Where(it => it.IsGenericType)
                                     .FirstOrDefault(it => it.GetGenericTypeDefinition() == genericBaseType);
            if (foundInterface != null) return foundInterface.GetGenericArguments();

            Type baseType = type.BaseType;
            if (baseType == null)
                return null;

            if (!baseType.IsGenericType)
                return GetGenericArgumentTypes(baseType, genericBaseType);

            if (baseType.GetGenericTypeDefinition() == genericBaseType)
                return baseType.GetGenericArguments();

            return null;
        }

        /// <summary>
        /// Gets the type of the list elements when <paramref name="listType"/> represents a list.
        /// </summary>
        /// <param name="listType">A type representing a list</param>
        /// <returns>The type of the list elements, or <see langword="null"/> if the type does not represent a list</returns>
        /// <remarks>
        /// The method returns the elements type when the given <paramref name="listType"/> is an Array or inherits from <see cref="IList{T}"/>
        /// </remarks>
        /// <example>
        /// <code source = "..\..\Systemorph.Utils.Test\Reflection\TypeExtensionsTest.cs" language="cs" region="GetListElementTypeSample" />
        /// </example>
        public static Type GetListElementType(this Type listType)
        {
            var elementType = listType.GetGenericArgumentTypes(typeof(IList<>));
            return elementType != null ? elementType.First() : null;
        }

        /// <summary>
        /// Gets all hierarchically inherited interfaces of <paramref name="type"/>
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static IEnumerable<Type> GetAllInterfaces(this Type type)
        {
            return type.GetInterfaces().SelectMany(i => i.RepeatOnce().Concat(i.GetAllInterfaces())).Distinct();
        }


        /// <summary>
        /// Gets the type of the enumerables elements when <paramref name="enumerableType"/> represents an enumerable type.
        /// </summary>
        /// <param name="enumerableType">A type representing an enumerable</param>
        /// <returns>The type of the enumerables elements, or <see langword="null"/> if the type does not represent an enumerable</returns>
        /// <remarks>
        /// The method returns the elements type when the given <paramref name="enumerableType"/> is an Array or inherits from <see cref="IEnumerable{T}"/>
        /// </remarks>
        /// <example>
        /// <code source = "..\..\Systemorph.Utils.Test\Reflection\TypeExtensionsTest.cs" language="cs" region="GetEnumerableElementTypeSample" />
        /// </example>
        public static Type GetEnumerableElementType(this Type enumerableType)
        {
            var elementType = enumerableType.GetGenericArgumentTypes(typeof(IEnumerable<>));
            return elementType != null ? elementType.First() : null;
        }

        //TODO: Check this
        internal static Type GetSequenceElementType(this Type seqType)
        {
            Type type;
            if (TryFindIEnumerable(seqType, out type))
                return type;
            return seqType;
        }

        private static bool TryFindIEnumerable(Type seqType, out Type result)
        {
            result = null;
            if (seqType == null || seqType == typeof(string))
                return false;

            if (seqType.IsArray)
            {
                result = seqType.GetElementType();
                return true;
            }

            if (seqType.IsGenericType)
            {
                foreach (Type arg in seqType.GetGenericArguments())
                {
                    Type ienum = typeof(IEnumerable<>).MakeGenericType(arg);
                    if (ienum.IsAssignableFrom(seqType))
                    {
                        result = arg;
                        return true;
                    }
                }
            }

            Type[] ifaces = seqType.GetInterfaces();
            if (ifaces.Length > 0)
            {
                foreach (Type iface in ifaces)
                {
                    if (TryFindIEnumerable(iface, out result))
                        return true;
                }
            }

            if (seqType.BaseType != null && seqType.BaseType != typeof(object))
            {
                return TryFindIEnumerable(seqType.BaseType, out result);
            }

            return false;
        }

        /// <summary>
        /// Tests if the given <paramref name="type"/> meets the constraints of the <paramref name="genericTypeArgument"/>
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <param name="genericTypeArgument">The type of a generic type parameter</param>
        /// <returns>True, if the given <paramref name="type"/> meets the constraints of the <paramref name="genericTypeArgument"/></returns>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="type"/> is <see langword="null"/></exception>
        /// <exception cref="ArgumentNullException">Thrown, if the <paramref name="genericTypeArgument"/> is <see langword="null"/></exception>
        /// <exception cref="ArgumentException">Thrown, if the <paramref name="genericTypeArgument"/> is not a generic </exception>
        /// <example>
        /// Given this generic class with one constrained type parameter
        /// <code source = "..\..\Systemorph.Utils.Test\Reflection\TypeExtensionsTest.cs" language="cs" region="GenericWithConstraint" />
        /// the method tests if a type meets the constraint
        /// <code source = "..\..\Systemorph.Utils.Test\Reflection\TypeExtensionsTest.cs" language="cs" region="MeetsConstraintsSample" />
        /// </example>
        public static bool MeetsConstraints(this Type type, Type genericTypeArgument)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (genericTypeArgument == null)
                throw new ArgumentNullException(nameof(genericTypeArgument));
            if (!genericTypeArgument.IsGenericParameter)
                throw new ArgumentException("Generic type definition is expected", nameof(genericTypeArgument));

            var constraints = genericTypeArgument.GetGenericParameterConstraints();
            return constraints.All(ct => ct.IsAssignableFrom(type));
        }

        /// <summary>
        /// Tests if the type is a Queryable
        /// </summary>
        /// <param name="type">The type to test</param>
        /// <returns><see langword="true"/> if the type is a Queryable, <see langword="false"/> otherwise</returns>
        public static bool IsQueryable(this Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            return typeof(IQueryable).IsAssignableFrom(type);
        }

        /// <summary>
        /// Converts a flat list of types into a dictionary where the key is the common base type contained in the list and the value
        /// contains all inheritors (including the common base type).
        /// </summary>
        /// <param name="flatTypeHierarchy">A set of types</param>
        /// <returns>A <see cref="IDictionary{TKey,TValue}"/> where the key is the common base type of all types in the value</returns>
        /// <remarks>
        /// This method can e.g. be used to "compress" a list of given types to their common base type, if the data for the base type
        /// and all inheritors should be loaded at once in a single query
        /// </remarks>
        /// <example>
        /// <code source = "..\..\Systemorph.Utils.Test\Reflection\TypeExtensionsTest.cs" language="cs" region="BuildTypeHierarchies" />
        /// </example>
        public static IDictionary<Type, IList<Type>> BuildTypeHierarchies(this ISet<Type> flatTypeHierarchy)
        {
            var dict = new Dictionary<Type, IList<Type>>();
            foreach (var type in flatTypeHierarchy.Where(t => t != null))
            {
                var toBeMerged = new List<Type>();
                var baseTypeFound = false;
                foreach (var keyValue in dict)
                {
                    if (keyValue.Key.IsAssignableFrom(type))
                    {
                        baseTypeFound = true;
                        keyValue.Value.Add(type);
                        break;
                    }

                    if (type.IsAssignableFrom(keyValue.Key))
                    {
                        baseTypeFound = true;
                        toBeMerged.Add(keyValue.Key);
                    }
                }

                if (toBeMerged.Count > 0)
                {
                    var mergedTypes = new List<Type> { type };
                    foreach (var childType in toBeMerged)
                    {
                        mergedTypes.AddRange(dict[childType]);
                        dict.Remove(childType);
                    }

                    dict.Add(type, mergedTypes);
                }

                if (!baseTypeFound)
                    dict.Add(type, new List<Type> { type });
            }

            return dict;
        }
    }

    /// <summary>
    /// Flags defining GetSpeakingName behavior
    /// </summary>
    [Flags]
    public enum SpeakingNameOptions
    {
        /// <summary>
        /// Default behavior, everything is included
        /// </summary>
        Default = int.MinValue,

        /// <summary>
        /// Namespace should be omitted
        /// </summary>
        OmitNamespaces = 1 << 0,

        /// <summary>
        /// Generic type arguments should be omitted
        /// </summary>
        OmitTypeArgNames = OmitNamespaces << 1,

        /// <summary>
        /// Both namespace and generic type arguments should be omitted
        /// </summary>
        OmitNamespaceAndTypeArgNames = OmitNamespaces | OmitTypeArgNames,

        /// <summary>
        /// Generic part of type name including braces should be omitted
        /// </summary>
        OmitBrackets = OmitTypeArgNames << 1
    }
}