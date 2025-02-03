using System;
using System.Collections.Generic;
using System.Linq;

namespace SaveCleaner;

public static class TypeFinder
{
    public static IEnumerable<Type> GetDerivedTypes<TBase>()
    {
        Type baseType = typeof(TBase);
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => baseType.IsAssignableFrom(t) && t != baseType && !t.IsAbstract);
    }
}