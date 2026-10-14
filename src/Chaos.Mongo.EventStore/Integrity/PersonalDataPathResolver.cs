// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson.Serialization;
using System.Reflection;

/// <summary>
/// Resolves personal data marks to the element paths of the stored events and validates them against the
/// registered class maps.
/// </summary>
internal static class PersonalDataPathResolver
{
    /// <summary>
    /// Resolves the marks of every event type to its distinct element paths.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="options">The event store options holding the marks.</param>
    /// <returns>The resolved element paths per event type.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a marked member is unmapped or non-nullable, when a segment before the last traverses an array or
    /// is serialized without resolvable member element names, or when two marked paths of one event type overlap.
    /// </exception>
    public static IReadOnlyDictionary<Type, IReadOnlyList<String>> Resolve<TAggregate>(MongoEventStoreOptions<TAggregate> options)
        where TAggregate : class, IAggregate, new()
    {
        ArgumentNullException.ThrowIfNull(options);

        var result = new Dictionary<Type, IReadOnlyList<String>>();
        foreach (var (eventType, marks) in options.PersonalDataMarks)
        {
            var paths = new List<String>(marks.Count);
            var marksByPath = new Dictionary<String, PersonalDataMark>(StringComparer.Ordinal);
            foreach (var mark in marks)
            {
                var path = ResolvePath(eventType, mark);
                if (marksByPath.TryAdd(path, mark))
                {
                    paths.Add(path);
                }
            }

            EnsureNoOverlap(eventType, paths, marksByPath);
            result[eventType] = paths;
        }

        return result;
    }

    private static InvalidOperationException CreateError(Type eventType, PersonalDataMark mark, String reason)
        => new($"Event type {eventType.Name} marks '{mark.Description}' as personal data, but {reason}");

    private static void EnsureNoOverlap(Type eventType, List<String> paths, Dictionary<String, PersonalDataMark> marksByPath)
    {
        for (var first = 0; first < paths.Count; first++)
        {
            for (var second = first + 1; second < paths.Count; second++)
            {
                var (shorter, longer) = paths[first].Length <= paths[second].Length
                    ? (paths[first], paths[second])
                    : (paths[second], paths[first]);

                if (longer.StartsWith(shorter + ".", StringComparison.Ordinal))
                {
                    throw CreateError(
                        eventType,
                        marksByPath[longer],
                        $"the path '{longer}' overlaps the path '{shorter}' marked by '{marksByPath[shorter].Description}'. " +
                        "A marked path must not be a prefix of another marked path of the same event type.");
                }
            }
        }
    }

    private static void EnsureNullable(Type eventType, PersonalDataMark mark, MemberInfo member)
    {
        var memberType = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        if (memberType.IsValueType)
        {
            if (Nullable.GetUnderlyingType(memberType) is null)
            {
                throw CreateError(eventType, mark, $"the member {member.Name} of type {memberType.Name} cannot hold null. " +
                                                   "A marked member must be nullable, because erasure sets its value to null.");
            }

            return;
        }

        var context = new NullabilityInfoContext();
        var nullability = member is PropertyInfo nullableProperty ? context.Create(nullableProperty) : context.Create((FieldInfo)member);
        if (nullability.ReadState == NullabilityState.NotNull || nullability.WriteState == NullabilityState.NotNull)
        {
            throw CreateError(eventType, mark, $"the member {member.Name} is annotated as non-nullable. " +
                                               "A marked member must be nullable, because erasure sets its value to null.");
        }
    }

    private static String ResolvePath(Type eventType, PersonalDataMark mark)
    {
        var serializer = BsonSerializer.LookupSerializer(eventType);
        var elementNames = new List<String>(mark.Members.Count);
        var segment = eventType.Name;

        for (var index = 0; index < mark.Members.Count; index++)
        {
            var member = mark.Members[index];
            if (serializer is not IBsonDocumentSerializer documentSerializer)
            {
                throw CreateError(eventType, mark, $"'{segment}' is serialized by {serializer.GetType().Name}, which cannot resolve " +
                                                   "member element names. Every segment before the last must be serialized as a document " +
                                                   "with mapped members, for example by a class map.");
            }

            if (!documentSerializer.TryGetMemberSerializationInfo(member.Name, out var serializationInfo))
            {
                throw CreateError(eventType, mark, $"the member {member.Name} of '{segment}' is not mapped.");
            }

            elementNames.Add(serializationInfo.ElementName);
            var isLast = index == mark.Members.Count - 1;
            if (!isLast && serializationInfo.Serializer is IBsonArraySerializer)
            {
                throw CreateError(eventType, mark, $"the member {member.Name} of '{segment}' is serialized as an array. " +
                                                   "Only the last segment of a marked path may be an array.");
            }

            serializer = serializationInfo.Serializer;
            segment = $"{segment}.{member.Name}";
        }

        EnsureNullable(eventType, mark, mark.Members[^1]);
        return String.Join(".", elementNames);
    }
}
