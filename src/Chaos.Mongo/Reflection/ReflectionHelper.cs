// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Reflection;

using System.Reflection;

/// <summary>
/// Reflection helper for internal use.
/// </summary>
internal static class ReflectionHelper
{
    /// <summary>
    /// Scans assemblies for concrete, public implementations of the specified interface type.
    /// </summary>
    /// <param name="interfaceType">
    /// The interface type to search implementations for.
    /// </param>
    /// <param name="assemblies">
    /// Optional collection of assemblies to scan. If not provided, all currently loaded assemblies will be scanned.
    /// </param>
    /// <returns>
    /// An enumerable of types that implement the specified interface.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="interfaceType"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="interfaceType"/> is not an interface.
    /// </exception>
    public static IEnumerable<Type> GetInterfaceImplementations(Type interfaceType, IEnumerable<Assembly>? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);

        if (!interfaceType.IsInterface)
            throw new ArgumentException($"Type {interfaceType.Name} must be an interface.", nameof(interfaceType));

        var assembliesToScan = assemblies ?? AppDomain.CurrentDomain.GetAssemblies();

        return assembliesToScan
               .SelectMany(assembly => assembly.GetTypes())
               .Where(type => interfaceType.IsAssignableFrom(type)
                              && type is { IsClass: true, IsAbstract: false }
                              && (type.IsPublic || type.IsNestedPublic));
    }
}
