using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Reflection
{
    /// <summary>
    /// The <see cref="ReflectionHelper"/> helps to simplify common tasks when working with Reflection
    /// </summary>
    public static class ReflectionHelper
    {
        public const string GetterPrefix = "get_";
        public const string SetterPrefix = "set_";
        private static readonly int PrefixLength = GetterPrefix.Length;


        public const BindingFlags PublicInstanceBindingFlags = BindingFlags.Instance | BindingFlags.Public;
        public const BindingFlags PrivateInstanceBindingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        public const BindingFlags AnyInstanceBindingFlags = PublicInstanceBindingFlags | PrivateInstanceBindingFlags;

        #region Extensions

        /// <summary>
        /// Tests, if the <paramref name="method"/> is a property accessor method (either getter or setter)
        /// </summary>
        /// <param name="method">The method to test</param>
        /// <returns>True, if the method is either a property getter or setter</returns>
        public static bool IsPropertyAccessor(this MethodInfo method)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            var name = method.Name;
            return name.StartsWith(GetterPrefix) || name.StartsWith(SetterPrefix);
        }

        /// <summary>
        /// Tests, if the <paramref name="method"/> is a property getter accessor method
        /// </summary>
        /// <param name="method">The method to test</param>
        /// <returns>True, if the method is a property getter</returns>        
        public static bool IsPropertyGetter(this MethodInfo method)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            var name = method.Name;
            return name.StartsWith(GetterPrefix);
        }

        /// <summary>
        /// Tests, if the <paramref name="method"/> is a property setter accessor method
        /// </summary>
        /// <param name="method">The method to test</param>
        /// <returns>True, if the method is a property setter</returns>        
        public static bool IsPropertySetter(this MethodInfo method)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            var name = method.Name;
            return name.StartsWith(SetterPrefix);
        }

        /// <summary>
        /// Gets the related property info for the given getter or setter <paramref name="accessor"/> method
        /// </summary>
        /// <param name="accessor">The getter or setter method</param>
        /// <param name="throwIfNotAccessor">Optional flag, defining if an exception is thrown in case, the <paramref name="accessor"/> method is no getter or setter</param>
        /// <returns>The related property info</returns>
        public static PropertyInfo GetProperty(this MethodInfo accessor, bool throwIfNotAccessor = true)
        {
            if (!accessor.IsPropertyAccessor())
            {
                if (!throwIfNotAccessor)
                    return null;
                else
                    throw new ArgumentException("Method is not accessor of a property");
            }

            var propertyName = accessor.Name.Substring(PrefixLength, accessor.Name.Length - PrefixLength);
            var indexerTypes = accessor.GetParameters().Select(x => x.ParameterType).ToArray();
            var bindingFlags = accessor.IsPublic ? PublicInstanceBindingFlags : AnyInstanceBindingFlags;
            return accessor.DeclaringType?.GetProperty(propertyName,
                                                      bindingFlags, null,
                                                      accessor.ReturnType, indexerTypes,
                                                      null);
        }

        public static Signature GetSignature(this MemberInfo member)
        {
            if (member == null)
                throw new ArgumentNullException(nameof(member));

            return new Signature(member);
        }

        /// <summary>
        /// Get all constants of type string defined in the declaring <paramref name="type"/>
        /// </summary>
        /// <param name="type">The declaring type containing string constants</param>
        /// <returns>A dictionary with all string constants with key = name of constant and value = value of constant</returns>        
        public static IDictionary<string, string> GetStringConstants(this Type type)
        {
            return GetConstants<string>(type);
        }

        /// <summary>
        /// Get all constants of type <typeparamref name="T"/> defined in the declaring <paramref name="type"/>
        /// </summary>
        /// <typeparam name="T">The type of the constants</typeparam>
        /// <param name="type">The declaring type containing constants</param>
        /// <returns>A dictionary with all constants of type <typeparamref name="T"/> with key = name of constant and value = value of constant</returns>
        public static IDictionary<string, T> GetConstants<T>(this Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var q = from f in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                    where f.IsLiteral
                    let value = f.GetValue(null)
                    where value is T
                    select new KeyValuePair<string, T>(f.Name, (T)value);

            return q.ToDictionary(x => x.Key, x => x.Value);
        }

        // TODO: Write same method for methods
        /// <summary>
        /// Tests if the array of types fullfill the constraints defined in the <paramref name="genericTypeDefinition"/>
        /// </summary>
        /// <param name="types">An array of types in the order of the generic type parameters of the <paramref name="genericTypeDefinition"/></param>
        /// <param name="genericTypeDefinition">A generic type which may have constraints defined on its type parameters</param>
        /// <returns>True, if the types can be used as type parameters in the the generic type</returns>
        /// <example>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="MatchesConstraintsOf" />
        /// </example>
        public static bool MatchesConstraintsOf(this Type[] types, Type genericTypeDefinition)
        {
            ValidateTypeSet(types);

            if (genericTypeDefinition == null)
                throw new ArgumentNullException(nameof(genericTypeDefinition));
            if (!genericTypeDefinition.IsGenericTypeDefinition)
                throw new ArgumentException("Generic type definition expected", nameof(genericTypeDefinition));

            var genericArguments = genericTypeDefinition.GetGenericArguments();
            if (types.Length != genericArguments.Length)
                throw new ArgumentException("Type count mismatch");

            var allMatch = types.Zip(genericArguments, MatchesConstraintsOf).All(x => x);
            return allMatch;
        }

        private static void ValidateTypeSet(Type[] types)
        {
            if (types == null)
                throw new ArgumentNullException(nameof(types));
            if (types.Length == 0)
                throw new ArgumentException("No types specified", nameof(types));
        }

        private static bool MatchesConstraintsOf(Type type, Type genericArgumentType)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (genericArgumentType == null)
                throw new ArgumentNullException(nameof(genericArgumentType));

            var constraints = genericArgumentType.GetGenericParameterConstraints();
            var attributes = genericArgumentType.GenericParameterAttributes;

            if (constraints.Any(constraint => !constraint.IsAssignableFrom(type)))
                return false;

            if (AttributesContain(attributes, GenericParameterAttributes.ReferenceTypeConstraint) && type.IsValueType)
                return false;

            if (AttributesContain(attributes, GenericParameterAttributes.NotNullableValueTypeConstraint) && !type.IsValueType)
                return false;

            if (AttributesContain(attributes, GenericParameterAttributes.DefaultConstructorConstraint) && type.GetConstructors().All(c => c.GetParameters().Length > 0))
                return false;

            const GenericParameterAttributes notImplementedYet =
                    GenericParameterAttributes.Contravariant
                    | GenericParameterAttributes.Covariant
                    | GenericParameterAttributes.VarianceMask
                    | GenericParameterAttributes.SpecialConstraintMask;

            if ((attributes & notImplementedYet) != default(GenericParameterAttributes))
                throw new NotImplementedException();

            return true;
        }

        private static bool AttributesContain(GenericParameterAttributes attributes, GenericParameterAttributes flag)
        {
            return (attributes & flag) == flag;
        }

        #endregion

        #region Fluent

        /// <summary>
        /// Gets the <see cref="ConstructorInfo"/> of a class defined in the selector expression
        /// </summary>
        /// <typeparam name="T">The type of the declaring type</typeparam>
        /// <param name="selector">An expression which selects a constructor</param>
        /// <returns>The <see cref="ConstructorInfo"/> of the class</returns>
        /// <example>
        /// <para>
        /// Given a simple class with a constructor having two parameters
        /// </para>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="GetConstructorTestClass" />
        /// <para>
        /// the method is used like this
        /// </para>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="GetConstructorUsage" />
        /// </example>
        /// <remarks>
        /// The <see cref="GetConstructor{T}"/> method has the great advantage to use expressions instead of hardcoded type values which 
        /// allow better support for compiler checks and refactoring.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a constructor expression</exception>
        public static ConstructorInfo GetConstructor<T>(Expression<Func<T>> selector)
        {
            var body = selector.Body;
            var expression = body as NewExpression;
            if (expression != null)
                return expression.Constructor;

            throw new ArgumentException("Constructor selector expected");
        }

        /// <summary>
        /// Gets the <see cref="FieldInfo"/> of the field defined in the selector expression
        /// </summary>
        /// <typeparam name="T">The type of the declaring type</typeparam>
        /// <param name="selector">An expression which selects a field</param>
        /// <returns>The <see cref="FieldInfo"/> of the selected field</returns>
        /// <example>
        /// The usage is similar to the <see cref="GetProperty{T}"/> method
        /// </example>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a field</exception>
        public static FieldInfo GetField<T>(Expression<Func<T, object>> selector)
        {
            return GetFieldInner(selector);
        }

        /// <summary>
        /// Gets the <see cref="FieldInfo"/> of the static field defined in the selector expression
        /// </summary>
        /// <param name="selector">An expression which selects a static field</param>
        /// <returns>The <see cref="FieldInfo"/> of the selected static field</returns>
        /// <example>
        /// The usage is similar to the <see cref="GetProperty{T}"/> method
        /// </example>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a static field</exception>        
        public static FieldInfo GetStaticField(Expression<Func<object>> selector)
        {
            return GetFieldInner(selector);
        }

        private static FieldInfo GetFieldInner<TDelegate>(Expression<TDelegate> selector)
        {
            var body = selector.Body;

            // in case of implicit cast of value type
            var unaryExpression = body as UnaryExpression;
            if (unaryExpression != null && unaryExpression.NodeType == ExpressionType.Convert && unaryExpression.Type == typeof(object))
                body = unaryExpression.Operand;

            var expression = body as MemberExpression;
            if (expression == null || !(expression.Member is FieldInfo))
                throw new ArgumentException("Field selector expected");

            return (FieldInfo)expression.Member;
        }

        /// <summary>
        /// Gets the <see cref="PropertyInfo"/> from type <typeparamref name="T"/> of the property defined in the selector expression
        /// </summary>
        /// <typeparam name="T">The type of the declaring type</typeparam>
        /// <param name="selector">An expression which selects a property</param>
        /// <returns>The <see cref="PropertyInfo"/> of the selected property</returns>
        /// <example>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="GetPropertyUsage" />
        /// <para>
        /// For a complete example how to use this method, see also <conceptualLink target="bacb4a31-1819-48be-85f7-08632b4879f5" />
        /// </para>
        /// </example>
        /// <remarks>
        /// The <see cref="GetProperty"/> method has the great advantage to use expressions instead of hardcoded string values which 
        /// allow better support for renaming and refactoring.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a property</exception>
        public static PropertyInfo GetProperty<T>(this Expression<Func<T, object>> selector)
        {
            return GetPropertyInner(selector, typeof(T));
        }

        /// <summary>
        /// Gets the <see cref="PropertyInfo"/> from type <typeparamref name="T"/> of the property defined in the selector expression
        /// </summary>
        /// <typeparam name="T">The type of the declaring type</typeparam>
        /// <typeparam name="TProperty">The type of the property</typeparam>
        /// <param name="selector">An expression which selects a property</param>
        /// <returns>The <see cref="PropertyInfo"/> of the selected property</returns>
        /// <example>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="GetPropertyUsage2" />
        /// </example>
        /// <remarks>
        /// The <see cref="GetProperty"/> method has the great advantage to use expressions instead of hardcoded string values which 
        /// allow better support for renaming and refactoring.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a property</exception>
        public static PropertyInfo GetProperty<T, TProperty>(this Expression<Func<T, TProperty>> selector)
        {
            return GetPropertyInner(selector, typeof(T));
        }

        /// <summary>
        /// Gets the <see cref="PropertyInfo"/> of the static property defined in the selector expression
        /// </summary>
        /// <param name="selector">An expression which selects a static property</param>
        /// <returns>The <see cref="PropertyInfo"/> of the selected static property</returns>
        /// <example>
        /// The usage is similar to the <see cref="GetProperty{T}"/> method
        /// </example>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a static property</exception>                
        public static PropertyInfo GetStaticProperty(Expression<Func<object>> selector)
        {
            return GetPropertyInner(selector);
        }

        private static PropertyInfo GetPropertyInner<TDelegate>(Expression<TDelegate> selector, Type type = null)
        {
            Expression expression;
            // if the return value had to be cast to object, the body will be an UnaryExpression
            var unary = selector.Body as UnaryExpression;
            if (unary != null)
                // the operand is the "real" property access
                expression = unary.Operand;
            else
                // in case the property is of type object the body itself is the correct expression
                expression = selector.Body;

            PropertyInfo property = null;

            var memberExpression = expression as MemberExpression;
            if (memberExpression != null)
            {
                property = memberExpression.Member as PropertyInfo;
            }
            else
            {
                var methodCallExpression = expression as MethodCallExpression;
                if (methodCallExpression != null)
                    property = methodCallExpression.Method.GetProperty(false);
            }

            if (property == null)
                throw new ArgumentException("Property selector expected");

            if (type != null && property.DeclaringType != type)
            {
                var getter = property.GetGetMethod();
                if (getter == null)
                    throw new NotSupportedException("Setter-only properties are not supported by this operation!");

                var flags = BindingFlags.Default;

                flags |= getter.IsStatic ? BindingFlags.Static : BindingFlags.Instance;
                flags |= getter.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic;

                return GetPropertyWorkaround(type, property.Name, flags, property.PropertyType);
            }

            return property;
        }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> from type <typeparamref name="T"/> of the method defined in the selector expression
        /// </summary>
        /// <typeparam name="T">The type of the declaring type</typeparam>
        /// <param name="selector">An expression which selects a method</param>
        /// <returns>The <see cref="MethodInfo"/> of the selected method</returns>
        /// <example>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="GetMethodUsage" />
        /// </example>
        /// The used parameters in the method expression are arbitrary place holders and do not influence the result
        /// <remarks>
        /// The <see cref="GetMethod{T}"/> method has the great advantage to use expressions instead of hardcoded string values which 
        /// allow better support for compiler checks, renaming and refactoring.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a method call</exception>
        public static MethodInfo GetMethod<T>(Expression<Action<T>> selector)
        {
            return GetMethodInner(selector, typeof(T));
        }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> of the static method defined in the selector expression
        /// </summary>
        /// <param name="selector">An expression which selects a static method</param>
        /// <returns>The <see cref="MethodInfo"/> of the selected static method</returns>
        /// <example>
        /// The usage is similar to <see cref="GetMethod{T}"/>
        /// </example>
        /// <remarks>
        /// The <see cref="GetMethod{T}"/> method has the great advantage to use expressions instead of hardcoded string values which 
        /// allow better support for compiler checks, renaming and refactoring.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a static method call</exception>
        public static MethodInfo GetStaticMethod(Expression<Action> selector)
        {
            return GetMethodInner(selector);
        }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> from type <typeparamref name="T"/> of the generic method defined in the selector expression
        /// </summary>
        /// <typeparam name="T">The type of the declaring type</typeparam>
        /// <param name="selector">An expression which selects a generic method</param>
        /// <returns>The <see cref="MethodInfo"/> of the selected generic method</returns>
        /// <example>
        /// <para>
        /// Given a class with a generic method
        /// </para>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="ClassWithGenericMethod" />
        /// <para>The method is used like this:</para>
        /// <code source = "..\..\Systemorph.Api.Help.CodeSnippets\Api\Utils\Reflection\ReflectionHelperSnippets.cs" language="cs" region="GetMethodGenericUsage" />
        /// </example>
        /// <para>The used type and method parameters in the method expression are arbitrary place holders and do not influence the result</para>
        /// <remarks>
        /// The <see cref="GetMethodGeneric{T}"/> method has the great advantage to use expressions instead of hardcoded string values which 
        /// allow better support for compiler checks, renaming and refactoring.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a generic method call</exception>        
        public static MethodInfo GetMethodGeneric<T>(Expression<Action<T>> selector)
        {
            return GetMethodInner(selector, typeof(T), true);
        }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> of the static generic method defined in the selector expression
        /// </summary>
        /// <param name="selector">An expression which selects a generic method</param>
        /// <returns>The <see cref="MethodInfo"/> of the selected generic method</returns>
        /// <example>
        /// The usage is similar to <see cref="GetMethodGeneric{T}"/>
        /// </example>
        /// The used type and method parameters in the method expression are arbitrary place holders and do not influence the result
        /// <remarks>
        /// The <see cref="GetStaticMethodGeneric"/> method has the great advantage to use expressions instead of hardcoded string values which 
        /// allow better support for compiler checks, renaming and refactoring.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a static generic method call</exception>        
        public static MethodInfo GetStaticMethodGeneric(Expression<Action> selector)
        {
            return GetMethodInner(selector, generic: true);
        }

        private static MethodInfo GetMethodInner<TDelegate>(Expression<TDelegate> selector, Type type = null, bool generic = false)
        {
            var body = selector.Body;
            var expression = body as MethodCallExpression;
            if (expression == null)
                throw new ArgumentException("Method selector expected");

            var member = expression.Method;
            if (type != null && member.DeclaringType != type && !member.DeclaringType.IsStatic())
            {
                var flags = BindingFlags.Default;
                flags |= member.IsStatic ? BindingFlags.Static : BindingFlags.Instance;
                flags |= member.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic;

                if (generic)
                    member = member.GetGenericMethodDefinition();

                var @params = member.GetSignature().ParameterTypes;
                return GetMethodWorkaround(type, member.Name, flags, @params);
            }

            return generic
                ? member.GetGenericMethodDefinition()
                : member;
        }

        private static MethodInfo GetMethodWorkaround(Type type, string name, BindingFlags bindingFlags, Type[] parameterTypes)
        {
            if (!type.IsInterface)
                return type.GetMethod(name, bindingFlags, null, parameterTypes, null);

            var q = from p in type.RepeatOnce().Union(type.GetInterfaces())
                    select p.GetMethod(name, bindingFlags, null, parameterTypes, null);

            return q.FirstOrDefault(p => p != null);
        }

        private static PropertyInfo GetPropertyWorkaround(Type type, string name, BindingFlags flags, Type propertyType)
        {
            if (!type.IsInterface)
                return type.GetProperty(name, flags, null, null, new Type[0], null);

            var q = from p in type.RepeatOnce().Union(type.GetInterfaces())
                    select p.GetProperty(name, flags, null, propertyType, new Type[0], null);

            return q.FirstOrDefault(p => p != null);
        }

        #endregion

        /// <summary>
        /// Gets the values of the enum
        /// </summary>
        /// <typeparam name="TEnum">Type of the enum</typeparam>
        /// <returns>All values of the enum</returns>
        public static IEnumerable<TEnum> GetEnumValues<TEnum>()
        {
            return Enum.GetValues(typeof(TEnum)).Cast<TEnum>();
        }

        public static bool IsDictionary(this Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>);
        }



    }


}
