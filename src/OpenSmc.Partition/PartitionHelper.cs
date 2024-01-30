using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Equality;
using OpenSmc.Collections;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Reflection;

namespace OpenSmc.Partition
{
    public static class PartitionHelper
    {
        public static readonly CreatableObjectStore<Type, Func<object, object>> PartitionIdSelectors = new(PartitionIdSelector);
        public static readonly CreatableObjectStore<Type, Type, Func<object, object>> PartitionKeyPropertyGetters = new(PartitionKeyPropertyGetter);
        public static readonly CreatableObjectStore<Type, Type, Func<object, object>> PartitionMaterializers = new(PartitionMaterializer);
        public static readonly CreatableObjectStore<Type, Action<object, object>> PartitionKeySetters = new(PartitionKeySetterInner);
        public static readonly CreatableObjectStore<Type, Type, Type, Func<object, object, bool>> PartitionIdentityPropertyComparers = new(PartitionIdentityPropertyComparer);
        public static readonly CreatableObjectStore<Type, PropertyInfo> PartitionIdProperties = new(GetPartitionIdProperty);
        public static readonly CreatableObjectStore<Type, PropertyInfo> PartitionKeyProperties = new(GetPartitionKeyProperty);
        public static readonly CreatableObjectStore<Type, (string Name, Type PartitionType)> AssociatedPartitionPerType = new(GetAssociatedPartitionInner);

        public static Action<object, object> GetPartitionKeySetter(Type instanceType)
        {
            //takes internal type of instance
            return PartitionKeySetters.GetInstance(instanceType);
        }

        public static object GetPartitionKeyByFromProperty(object instance, Type instanceType)
        {
            if (instance == null)
                return null;
            var getter = PartitionKeyPropertyGetters.GetInstance(instanceType, instanceType);
            if (getter != null)
            {
                var value = getter.Invoke(instance);
                if (!IsDefaultValue(value))
                    return value;
            }

            return null;
        }

        public static bool IsValueTypePartition(Type partitionType)
        {
            return partitionType.IsValueType || partitionType == typeof(string);
        }

        public static bool IsDefaultValue(object value)
        {
            if (value == null)
                return true;

            if (value.GetType().HasNullAsDefaultValue())
                return false;

            if (value is Guid guidId)
                return guidId == default;
            if (value is DateTime date)
                return date == default;
           
            return false;
        }


        private static (string, Type) GetAssociatedPartitionInner(Type type)
        {
            var property = PartitionKeyProperties.GetInstance(type);
            if (property == null)//poco does not contain partitionId, so we give attempt to find it on related Entity type
                return (null, null); //unPartitioned

            var partitionKeyAttribute = property.GetSingleCustomAttribute<PartitionKeyAttribute>();

            // if no type set in attribute, then property type is a partition type!
            var partitionType = partitionKeyAttribute.PartitionType ?? property.PropertyType;
            // if no name set in attribute, then name will be equal to type name
            var partitionName = partitionKeyAttribute.PartitionName ?? partitionType.Name;

            if (property.HasAttribute<IdentityPropertyAttribute>())
                throw new NotSupportedException(string.Format(PartitionErrorMessages.PartitionKeyPropertyHasIdentityAttribute, property.Name, type.Name));

            return (partitionName, partitionType);
        }
        private static Action<object, object> PartitionKeySetterInner(Type instanceType)
        {
            var partitionKeyProperty = PartitionKeyProperties.GetInstance(instanceType);
            if (partitionKeyProperty == null || !partitionKeyProperty.CanWrite)
                return null;

            var instanceParameter = Expression.Parameter(typeof(object));
            var partitionParameter = Expression.Parameter(typeof(object));
            var instanceConvert = Expression.Convert(instanceParameter, instanceType);
            var partitionConvert = Expression.Convert(partitionParameter, partitionKeyProperty.PropertyType);

            var assign = Expression.Assign(Expression.Property(instanceConvert, partitionKeyProperty), partitionConvert);
            var lambda = Expression.Lambda<Action<object, object>>(assign, instanceParameter, partitionParameter);
            return lambda.Compile();
        }
        //related type is a type where we ask for partitionKey property. Can be either related entity type or itself.
        private static Func<object, object> PartitionKeyPropertyGetter(Type type, Type relatedType)
        {
            var parameter = Expression.Parameter(typeof(object));

            var partitionProperty = PartitionKeyProperties.GetInstance(relatedType);

            if (partitionProperty == null)
                return null;

            var pocoPartitionProperty = type.GetProperty(partitionProperty.Name);

            if (pocoPartitionProperty == null)
            {
                var mappedProperty = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(p => p.GetSingleCustomAttribute<MapToAttribute>()?.PropertyName == partitionProperty.Name);
                if (mappedProperty != null)
                {
                    pocoPartitionProperty = mappedProperty;
                }
                else
                {
                    return null;
                }
            }

            var propertyExpression = Expression.Property(Expression.Convert(parameter, type), pocoPartitionProperty);
            var conversion = Expression.Convert(propertyExpression, typeof(object));
            return Expression.Lambda<Func<object, object>>(conversion, parameter).Compile();
        }

        private static Func<object, object> PartitionMaterializer(Type instanceType, Type partitionType)
        {
            if (partitionType == null || IsValueTypePartition(partitionType))
                return null;

            var parameter = Expression.Parameter(typeof(object));

            var identityProperties = partitionType.GetIdentityProperties();
            var instanceTypePropertyNames = instanceType.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(p => p.GetSingleCustomAttribute<MapToAttribute>()?.PropertyName ?? p.Name, p => p.Name);

            var absentProperties = identityProperties.Select(p => p.Name).Except(instanceTypePropertyNames.Keys).ToArray();
            if (absentProperties.Any())
                return null;

            var convertToPartitionType = Expression.Convert(parameter, instanceType);
            var assignments = identityProperties.Select(p => Expression.Bind(p, Expression.Property(convertToPartitionType, instanceTypePropertyNames[p.Name])));

            var constructorInfo = partitionType.GetConstructors().MinBy(x => x.GetParameters().Length);
            var constructorParameters = constructorInfo.GetParameters().Select(x => x.Name).Select(x=> Expression.Property(convertToPartitionType, instanceTypePropertyNames[x]));

            var newExpression = Expression.New(constructorInfo, constructorParameters);
            var memberInit = Expression.MemberInit(newExpression, assignments);
            var lambda = Expression.Lambda<Func<object, object>>(memberInit, parameter);
            return lambda.Compile();
        }

        private static Func<object, object, bool> PartitionIdentityPropertyComparer(Type inputType, Type storedInstanceType, Type partitionType)
        {
            if (storedInstanceType == null || inputType == null)
                return (_, _) => false;

            if (IsValueTypePartition(inputType) || IsValueTypePartition(storedInstanceType))
            {
                if (inputType == storedInstanceType)
                    return (o1, o2) => o1.Equals(o2);
                return (_, _) => false;
            }

            var identityProperties = partitionType.GetIdentityProperties().Select(p => p.Name).ToArray();
            if (!identityProperties.Any())
            {
                if (inputType == storedInstanceType)
                    return (o1, o2) => o1.Equals(o2);
                throw new ArgumentException(string.Format(PartitionErrorMessages.MissingIdentityPropertiesAndNotSameTypes, storedInstanceType.Name, inputType.Name));
            }
            var instanceTypePropertyNames = inputType.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name);

            var absentProperties = identityProperties.Except(instanceTypePropertyNames).ToArray();
            if (absentProperties.Any())
                throw new ArgumentException(string.Format(PartitionErrorMessages.MissingIdentityProperties, inputType.Name, string.Join(",", absentProperties)));

            var inputPrm = Expression.Parameter(typeof(object));
            var partitionPrm = Expression.Parameter(typeof(object));
            var convertedInputPrm = Expression.Convert(inputPrm, inputType);
            var convertedPartitionPrm = Expression.Convert(partitionPrm, storedInstanceType);

            //if both null => true, if both not null and identity props are equal => true
            var equalityExpr = Expression.Or(Expression.AndAlso(Expression.Equal(convertedInputPrm, Expression.Constant(null, inputType)),
                                                                Expression.Equal(convertedPartitionPrm, Expression.Constant(null, storedInstanceType))),
                                             Expression.AndAlso(Expression.AndAlso(Expression.Not(Expression.Equal(convertedInputPrm, Expression.Constant(null, inputType))),
                                                                                   Expression.Not(Expression.Equal(convertedPartitionPrm, Expression.Constant(null, storedInstanceType)))),
                                                                identityProperties
                                                                    .Select(p => Expression.Equal(Expression.Property(convertedInputPrm, p),
                                                                                                  Expression.Property(convertedPartitionPrm, p)))
                                                                    .Aggregate(Expression.AndAlso)));
            var lambda = Expression.Lambda<Func<object, object, bool>>(equalityExpr, inputPrm, partitionPrm);
            return lambda.Compile();
        }

        private static Func<object, object> PartitionIdSelector(Type type)
        {
            var partitionIdProperty = PartitionIdProperties.GetInstance(type);

            var parameterObj = Expression.Parameter(typeof(object));
            var parameterConverted = Expression.Convert(parameterObj, type);
            var propertyExpression = Expression.Property(parameterConverted, partitionIdProperty);
            var conversion = Expression.Convert(propertyExpression, typeof(object));
            var lambda = Expression.Lambda<Func<object, object>>(conversion, parameterObj);
            return lambda.Compile();
        }

        private static PropertyInfo GetPartitionKeyProperty(Type type)
        {
            var partitionProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(x => x.HasAttribute<PartitionKeyAttribute>()).ToArray();
            var count = partitionProperties.Length;
            if (count > 1)
                throw new NotSupportedException(string.Format(PartitionErrorMessages.AmbiguousPartitions, type.Name));
            if (count == 0)
                return null; // unpartitioned
            return partitionProperties.Single();
        }

        private static PropertyInfo GetPartitionIdProperty(Type type)
        {
            var partitionIdProperty = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(p => p.HasAttribute<PartitionIdAttribute>());

            if (partitionIdProperty == null)
                throw new NotSupportedException(string.Format(PartitionErrorMessages.PartitionIdIsNotSpecified, type.Name));

            if (partitionIdProperty.HasAttribute<IdentityPropertyAttribute>())
                throw new NotSupportedException(string.Format(PartitionErrorMessages.PartitionIdPropertyHasIdentityAttribute, partitionIdProperty.Name, type.Name));

            return partitionIdProperty;
        }
    }
}
