// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using NUnit.Framework;
using System.Reflection;
using System.Runtime.CompilerServices;

/// <summary>
/// Lists every public type and member of the tamper-evidence feature and compares the listing with the approved one, so
/// every change to the public surface is deliberate. Run with <c>UPDATE_PUBLIC_SURFACE=1</c> to rewrite the approved
/// listing after a deliberate change, and review its diff.
/// </summary>
public class PublicIntegritySurfaceTests
{
    private const String ApprovedFile = "PublicIntegritySurface.approved.txt";

    [Test]
    public void ImplementationDetails_StayNonPublic()
    {
        typeof(Event<LedgerAggregate>).GetProperty("Integrity", BindingFlags.Instance | BindingFlags.NonPublic).Should().NotBeNull();
        typeof(CheckpointDocument<LedgerAggregate>).GetProperty("ChainHash", BindingFlags.Instance | BindingFlags.NonPublic).Should().NotBeNull();
        typeof(MongoBuilderExtensions).GetMethod("WithAnchorCandidates", BindingFlags.Static | BindingFlags.NonPublic).Should().NotBeNull();
        typeof(EventIntegrity).IsVisible.Should().BeFalse();
        typeof(AnchorVerifier).IsVisible.Should().BeFalse();
        typeof(IntegrityAnchoringOptions).IsVisible.Should().BeFalse();
    }

    [Test]
    public void PublicSurface_MatchesApprovedListing()
    {
        var path = Path.Combine(TamperEvidenceVectorTests.RepositoryRoot, "tests", "Chaos.Mongo.EventStore.Tests", "Integrity", ApprovedFile);
        var actual = String.Join(Environment.NewLine, DescribeSurface()) + Environment.NewLine;
        if (Environment.GetEnvironmentVariable("UPDATE_PUBLIC_SURFACE") == "1")
        {
            File.WriteAllText(path, actual);
        }

        actual.Should().Be(File.ReadAllText(path).ReplaceLineEndings(Environment.NewLine));
    }

    private static String Accessors(PropertyInfo property)
    {
        var accessors = new List<String>();
        if (property.GetMethod is { IsPublic: true })
        {
            accessors.Add("get;");
        }

        if (property.SetMethod is { IsPublic: true } setter)
        {
            accessors.Add(setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? "init;" : "set;");
        }

        return String.Join(" ", accessors);
    }

    private static String BaseTypes(Type type)
    {
        var bases = new List<Type>();
        if (type.BaseType is { } baseType && baseType != typeof(Object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
        {
            bases.Add(baseType);
        }

        bases.AddRange(type.GetInterfaces().Where(i => i.IsPublic && i.Namespace?.StartsWith("System", StringComparison.Ordinal) == false));
        return bases.Count == 0 ? String.Empty : " : " + String.Join(", ", bases.Select(b => Name(b)));
    }

    /// <summary>
    /// Formats a parameter's default value as a C# literal.
    /// </summary>
    private static String DefaultValue(ParameterInfo parameter)
        => parameter.DefaultValue switch
        {
            null => parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null ? "default" : "null",
            String text => $"\"{text}\"",
            Boolean flag => flag ? "true" : "false",
            Enum value => $"{Name(value.GetType())}.{value}",
            var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
        };

    private static IEnumerable<String> DescribeMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var nullability = new NullabilityInfoContext();
        foreach (var member in type.GetMembers(flags))
        {
            var description = member switch
            {
                ConstructorInfo constructor => $"ctor({Parameters(constructor, nullability)})",
                MethodInfo { IsSpecialName: true } => null,
                MethodInfo method => $"{Modifiers(method)}{Name(method.ReturnType, nullability.Create(method.ReturnParameter))} {method.Name}{GenericArguments(method)}" +
                                     $"({Parameters(method, nullability)})",
                PropertyInfo property => $"{(property.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : "")}" +
                                         $"{Name(property.PropertyType, nullability.Create(property))} {property.Name} {{ {Accessors(property)} }}",
                FieldInfo { IsLiteral: true } field when type.IsEnum =>
                    $"{field.Name} = {Convert.ToInt64(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}",
                FieldInfo { IsSpecialName: true } => null,
                FieldInfo field => $"{(field.IsLiteral ? "const " : field.IsStatic ? "static " : "")}{Name(field.FieldType, nullability.Create(field))} {field.Name}" +
                                   $"{(field.IsLiteral ? " = " + Literal(field.GetRawConstantValue()) : "")}",
                _ => null
            };
            if (description is not null)
            {
                yield return $"  {description}";
            }
        }
    }

    private static IEnumerable<String> DescribeSurface()
    {
        var integrityTypes = typeof(IEventStoreIntegrity<>).Assembly.GetExportedTypes()
                                                           .Where(t => t.Namespace == typeof(IEventStoreIntegrity<>).Namespace)
                                                           .Append(typeof(EventRegistrationBuilder<,>))
                                                           .Append(typeof(IntegrityProtectionBuilder<>))
                                                           .Append(typeof(MongoBuilderExtensions))
                                                           .Append(typeof(MongoEventStoreBuilder<>))
                                                           .Append(typeof(MongoEventStoreOptions<>))
                                                           .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var type in integrityTypes)
        {
            yield return $"{Kind(type)} {Name(type)}{BaseTypes(type)}";
            foreach (var member in DescribeMembers(type).Order(StringComparer.Ordinal))
            {
                yield return member;
            }
        }
    }

    private static String GenericArguments(MethodInfo method)
        => method.IsGenericMethodDefinition ? $"<{String.Join(", ", method.GetGenericArguments().Select(a => Name(a)))}>" : String.Empty;

    private static String Kind(Type type)
        => type.IsInterface ? "interface" :
            type.IsEnum ? "enum" :
            type.IsValueType ? "struct" :
            type.IsAbstract && type.IsSealed ? "static class" :
            type.IsSealed ? "sealed class" :
            type.IsAbstract ? "abstract class" : "class";

    private static String Literal(Object? value)
        => value switch
        {
            null => "null",
            String text => $"\"{text}\"",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
        };

    private static String Modifiers(MethodInfo method)
        => method.IsStatic ? "static " :
            method.DeclaringType is { IsInterface: false } && method.IsAbstract ? "abstract " :
            method.IsVirtual && !method.IsFinal && method.DeclaringType is { IsInterface: false } ? "virtual " : "";

    /// <summary>
    /// Names a type as C# writes it, with <c>?</c> for nullable value types and, when nullability information is given,
    /// for nullable references.
    /// </summary>
    private static String Name(Type type, NullabilityInfo? nullability = null)
    {
        if (type is { IsByRef: true } && type.GetElementType() is { } referenced)
        {
            return Name(referenced, nullability?.ElementType ?? nullability) + "&";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying, nullability?.GenericTypeArguments.FirstOrDefault()) + "?";
        }

        var suffix = !type.IsValueType && nullability is { ReadState: NullabilityState.Nullable } ? "?" : "";
        if (type is { IsArray: true } && type.GetElementType() is { } element)
        {
            return Name(element, nullability?.ElementType) + "[]" + suffix;
        }

        if (type.IsGenericParameter)
        {
            return type.Name + suffix;
        }

        var name = type.DeclaringType is { } declaringType ? $"{Name(declaringType)}.{type.Name}" : type.Name;
        if (!type.IsGenericType)
        {
            return name + suffix;
        }

        var arguments = type.GetGenericArguments()
                            .Select((argument, index) => Name(argument, nullability is { GenericTypeArguments.Length: > 0 } ? nullability.GenericTypeArguments[index] : null));
        return $"{name[..name.IndexOf('`')]}<{String.Join(", ", arguments)}>{suffix}";
    }

    private static String Parameters(MethodBase method, NullabilityInfoContext nullability)
        => String.Join(", ", method.GetParameters().Select(p => $"{Name(p.ParameterType, nullability.Create(p))} {p.Name}{(p.HasDefaultValue ? " = " + DefaultValue(p) : "")}"));
}
