﻿using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Json.Assertions;

public class JsonEquivalencyOptions
{
    public bool ExcludedTypeDiscriminator;
    public Dictionary<Type, List<string>> ExcludedProperties = new();

    public JsonEquivalencyOptions ExcludeProperty<T, TProp>(Expression<Func<T, TProp>> property)
    {
        var propertyInfo = (PropertyInfo)((MemberExpression)property.Body).Member;
        return ExcludeProperty(propertyInfo);
    }
    public JsonEquivalencyOptions ExcludeProperty(PropertyInfo propertyInfo)
    {
        if (propertyInfo.DeclaringType == null)
            throw new ArgumentException("Property has no declaring type");

        if (!ExcludedProperties.TryGetValue(propertyInfo.DeclaringType, out var list))
        {
            list = ExcludedProperties[propertyInfo.DeclaringType] = new List<string>();
        }
        list.Add(propertyInfo.Name?.ToCamelCase() ?? string.Empty);
        return this;
    }
    public JsonEquivalencyOptions ExcludeTypeDiscriminator(bool flag = true)
    {
        ExcludedTypeDiscriminator = flag;
        return this;
    }

}
