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
    /// Resolves the marks of every registered event type to its distinct element paths. A registered event type
    /// inherits the marks of its registered base event types whose first member its class map maps, so a derived
    /// event cannot store marked values without committing to them. An inherited member that is not mapped is not
    /// stored, which is the case when the event store registers the class map on top of <see cref="Event{TAggregate}"/>.
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
        foreach (var eventType in options.EventTypes.Keys)
        {
            var marks = CollectMarks(eventType, options.PersonalDataMarks);
            if (marks.Count == 0)
            {
                continue;
            }

            var paths = new List<String>(marks.Count);
            var marksByPath = new Dictionary<String, PersonalDataMark>(StringComparer.Ordinal);
            foreach (var (mark, inherited) in marks)
            {
                if (inherited && !IsFirstMemberMapped(eventType, mark))
                {
                    continue;
                }

                var path = ResolvePath(eventType, mark);
                if (marksByPath.TryAdd(path, mark))
                {
                    paths.Add(path);
                }
            }

            if (paths.Count == 0)
            {
                continue;
            }

            EnsureNoOverlap(eventType, paths, marksByPath);
            result[eventType] = paths;
        }

        return result;
    }

    /// <summary>
    /// Collects the marks of an event type and of its base types, base types first.
    /// </summary>
    private static List<(PersonalDataMark Mark, Boolean Inherited)> CollectMarks(Type eventType,
                                                                                 Dictionary<Type, List<PersonalDataMark>> marksByType)
    {
        var marks = new List<(PersonalDataMark Mark, Boolean Inherited)>();
        for (var type = eventType; type is not null; type = type.BaseType)
        {
            if (marksByType.TryGetValue(type, out var typeMarks))
            {
                var inherited = type != eventType;
                marks.InsertRange(0, typeMarks.Select(mark => (mark, inherited)));
            }
        }

        return marks;
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

    private static Boolean IsFirstMemberMapped(Type eventType, PersonalDataMark mark)
        => BsonSerializer.LookupSerializer(eventType) is IBsonDocumentSerializer documentSerializer &&
           documentSerializer.TryGetMemberSerializationInfo(mark.Members[0].Name, out _);

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

            // A dotted element name would be read as a nested path, so the value could never be committed or erased.
            if (serializationInfo.ElementName.Contains('.'))
            {
                throw CreateError(eventType, mark, $"the member {member.Name} of '{segment}' is mapped to the element name " +
                                                   $"'{serializationInfo.ElementName}', which contains a dot. Marked paths cannot address " +
                                                   "elements whose names contain dots.");
            }

            // Update paths cannot address a dollar-prefixed element, so the value could be committed but never erased.
            if (serializationInfo.ElementName.StartsWith('$'))
            {
                throw CreateError(eventType, mark, $"the member {member.Name} of '{segment}' is mapped to the element name " +
                                                   $"'{serializationInfo.ElementName}', which starts with a dollar sign. Marked paths cannot " +
                                                   "address elements whose names start with a dollar sign, because erasure cannot update them.");
            }

            elementNames.Add(serializationInfo.ElementName);
            var isLast = index == mark.Members.Count - 1;

            // A dictionary serializer resolves every name as a key rather than a mapped member, so it is rejected
            // explicitly instead of relying on the array interface it also implements.
            if (!isLast && serializationInfo.Serializer is IBsonDictionarySerializer)
            {
                throw CreateError(eventType, mark, $"the member {member.Name} of '{segment}' is serialized as a dictionary, whose " +
                                                   "keys are not mapped members. Only the last segment of a marked path may be a dictionary.");
            }

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
