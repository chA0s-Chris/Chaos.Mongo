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

        bases.AddRange(type.GetInterfaces().Where(i => i.IsPublic && !i.Namespace!.StartsWith("System", StringComparison.Ordinal)));
        return bases.Count == 0 ? String.Empty : " : " + String.Join(", ", bases.Select(Name));
    }

    private static IEnumerable<String> DescribeMembers(Type type, Func<MemberInfo, Boolean>? include = null)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var member in type.GetMembers(flags).Where(m => include?.Invoke(m) ?? true))
        {
            var description = member switch
            {
                ConstructorInfo constructor => $"ctor({Parameters(constructor)})",
                MethodInfo { IsSpecialName: true } => null,
                MethodInfo method => $"{(method.IsStatic ? "static " : "")}{Name(method.ReturnType)} {method.Name}{GenericArguments(method)}({Parameters(method)})",
                PropertyInfo property => $"{Name(property.PropertyType)} {property.Name} {{ {Accessors(property)} }}",
                FieldInfo field => $"{(field.IsLiteral ? "const " : field.IsStatic ? "static " : "")}{Name(field.FieldType)} {field.Name}",
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
                                                           .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var type in integrityTypes)
        {
            yield return $"{Kind(type)} {Name(type)}{BaseTypes(type)}";
            foreach (var member in DescribeMembers(type).Order(StringComparer.Ordinal))
            {
                yield return member;
            }
        }

        var integrityMembers = new Dictionary<Type, String[]>
        {
            [typeof(MongoBuilderExtensions)] = ["WithIntegrityAnchoring", "WithIntegrityVerification"],
            [typeof(MongoEventStoreBuilder<>)] = ["WithIntegrityProtection", "WithEvent"],
            [typeof(MongoEventStoreOptions<>)] = ["IntegrityProtectionEnabled", "SealingChunkSize", "SealingSweepInterval", "SealingSweepRetryDelay"]
        };
        foreach (var (type, names) in integrityMembers.OrderBy(pair => pair.Key.FullName, StringComparer.Ordinal))
        {
            yield return $"{Kind(type)} {Name(type)} (integrity members)";
            foreach (var member in DescribeMembers(type, m => names.Contains(m.Name)).Order(StringComparer.Ordinal))
            {
                yield return member;
            }
        }
    }

    private static String GenericArguments(MethodInfo method)
        => method.IsGenericMethodDefinition ? $"<{String.Join(", ", method.GetGenericArguments().Select(Name))}>" : String.Empty;

    private static String Kind(Type type)
        => type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class" : "class";

    private static String Name(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        var name = type.IsNested ? $"{Name(type.DeclaringType!)}.{type.Name}" : type.Name;
        if (!type.IsGenericType)
        {
            return name;
        }

        return $"{name[..name.IndexOf('`')]}<{String.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }

    private static String Parameters(MethodBase method)
        => String.Join(", ", method.GetParameters().Select(p => $"{Name(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")}"));
}
