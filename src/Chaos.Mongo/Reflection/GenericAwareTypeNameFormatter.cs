// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Reflection;

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
    /// stripped) followed by its recursively formatted type arguments in angle brackets; for an array type, its
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
            var definitionName = baseName(type.GetGenericTypeDefinition());
            var simpleName = GenericArityMarker.Replace(definitionName, String.Empty);
            var typeArguments = type.GetGenericArguments().Select(argument => FormatGenericAwareName(argument, baseName));

            return $"{simpleName}<{String.Join(",", typeArguments)}>";
        }

        return baseName(type);
    }
}
