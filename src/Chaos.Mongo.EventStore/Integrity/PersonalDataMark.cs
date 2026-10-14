// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Reflection;

/// <summary>
/// A member chain of an event type marked as personal data, as given at registration and before its element
/// path is resolved through the class maps.
/// </summary>
internal sealed class PersonalDataMark
{
    public PersonalDataMark(IReadOnlyList<MemberInfo> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            throw new ArgumentException("A personal data mark needs at least one member.", nameof(members));
        }

        Members = members;
    }

    /// <summary>
    /// Gets the member chain in CLR notation, for example <c>Recipient.Name</c>.
    /// </summary>
    public String Description => String.Join(".", Members.Select(member => member.Name));

    /// <summary>
    /// Gets the members from the event type down to the marked member.
    /// </summary>
    public IReadOnlyList<MemberInfo> Members { get; }
}
