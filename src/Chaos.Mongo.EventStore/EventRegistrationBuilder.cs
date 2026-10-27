// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Integrity;
using System.Linq.Expressions;
using System.Reflection;

/// <summary>
/// Configures the registration of one event type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class EventRegistrationBuilder<TAggregate, TEvent>
    where TAggregate : class, IAggregate, new()
    where TEvent : Event<TAggregate>
{
    private readonly List<PersonalDataMark> _marks;

    internal EventRegistrationBuilder(List<PersonalDataMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        _marks = marks;
    }

    /// <summary>
    /// Marks a member of the event as personal data. With integrity protection, the chain hash covers a salted
    /// commitment in place of the member's value, so the value can be erased later without breaking the chain.
    /// </summary>
    /// <remarks>
    /// The member is given as a plain member-access chain, for example <c>e => e.Recipient.Name</c>. It is
    /// validated against the class maps when the event store is registered: it must be mapped and nullable, no
    /// segment before the last may be an array, and every segment before the last must be serialized as a document
    /// with mapped members.
    /// </remarks>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="member">The member-access chain selecting the member.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="member"/> is not a plain member-access chain.</exception>
    public EventRegistrationBuilder<TAggregate, TEvent> WithPersonalData<TMember>(Expression<Func<TEvent, TMember>> member)
    {
        ArgumentNullException.ThrowIfNull(member);

        var members = new List<MemberInfo>();
        var expression = member.Body;
        while (expression is MemberExpression { Member: PropertyInfo or FieldInfo } memberExpression)
        {
            members.Insert(0, memberExpression.Member);
            expression = memberExpression.Expression;
        }

        if (members.Count == 0 || !ReferenceEquals(expression, member.Parameters[0]))
        {
            throw new ArgumentException(
                $"Personal data of event type {typeof(TEvent).Name} must be marked with a plain member-access chain " +
                $"such as e => e.Recipient.Name, but '{member}' was given.",
                nameof(member));
        }

        _marks.Add(new PersonalDataMark(members));
        return this;
    }
}
