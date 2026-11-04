// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Reflection;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Formats a <see cref="Type"/>'s name so that a closed generic type includes its type arguments, without ever
/// embedding assembly-qualified names, unlike <see cref="Type.FullName"/> for a closed generic or array type.
/// </summary>
internal static class GenericAwareTypeNameFormatter
{
    private static readonly Regex GenericArityMarker = new("`\\d+", RegexOptions.Compiled);

    /// <summary>
    /// Formats <paramref name="type"/>'s name, recursively expanding a closed generic type's type arguments and an
    /// array type's element type, using <paramref name="baseName"/> for every type shape that is neither.
    /// </summary>
    /// <param name="type">The type to format. Must be a closed (non-open, non-generic-parameter) runtime type.</param>
    /// <param name="baseName">Resolves the name of a type that is neither a closed generic type nor an array type.</param>
    /// <returns>
    /// The formatted name: for a closed generic type, its generic type definition's base name (arity marker
    /// stripped) followed by its recursively formatted type arguments in angle brackets, where a type nested in a
    /// generic type is formatted as its outermost generic declaring type's base name followed by each nested
    /// level's simple name, joined with <c>+</c>, and each type argument is attached to the level that declares it
    /// (e.g. <c>Outer&lt;A&gt;+Inner&lt;B&gt;</c>); for an array type, its
    /// recursively formatted element name with the array's rank/jagged suffix reconstructed around it; otherwise,
    /// <paramref name="baseName"/> applied to <paramref name="type"/> unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> or <paramref name="baseName"/> is null.</exception>
    public static String FormatGenericAwareName(Type type, Func<Type, String> baseName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(baseName);

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var rank = type.GetArrayRank();
            var suffix = rank == 1 ? "[]" : $"[{new String(',', rank - 1)}]";

            return $"{FormatGenericAwareName(elementType, baseName)}{suffix}";
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            return FormatClosedGenericName(type, baseName);
        }

        return baseName(type);
    }

    private static String FormatClosedGenericName(Type type, Func<Type, String> baseName)
    {
        // A type nested in a generic type inherits its declaring types' type arguments, so they are assigned to the
        // declaring type that owns them; otherwise Outer<A>.Inner and a non-nested Inner<A> would format identically.
        var definition = type.GetGenericTypeDefinition();
        var levels = new Stack<Type>();
        levels.Push(definition);

        while (definition.DeclaringType is { IsGenericType: true } declaringType)
        {
            definition = declaringType;
            levels.Push(definition);
        }

        var typeArguments = type.GetGenericArguments();
        var builder = new StringBuilder();
        var consumedArguments = 0;
        var isOutermostLevel = true;

        foreach (var level in levels)
        {
            var levelName = isOutermostLevel ? baseName(level) : level.Name;
            var levelArgumentCount = level.GetGenericArguments().Length;

            if (!isOutermostLevel)
            {
                builder.Append('+');
            }

            builder.Append(GenericArityMarker.Replace(levelName, String.Empty));

            if (levelArgumentCount > consumedArguments)
            {
                var ownArguments = typeArguments[consumedArguments..levelArgumentCount]
                    .Select(argument => FormatGenericAwareName(argument, baseName));
                builder.Append('<').Append(String.Join(",", ownArguments)).Append('>');
                consumedArguments = levelArgumentCount;
            }

            isOutermostLevel = false;
        }

        return builder.ToString();
    }
}
