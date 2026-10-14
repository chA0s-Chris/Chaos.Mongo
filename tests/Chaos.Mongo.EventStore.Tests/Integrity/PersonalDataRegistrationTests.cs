// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using NUnit.Framework;

/// <remarks>
/// Class-map registration is process-global and irreversible, so every test uses dedicated
/// aggregate and event types that no other test registers.
/// </remarks>
public class PersonalDataRegistrationTests
{
    [Test]
    public void EventIntegrityClassMap_WithoutPersonalData_OmitsTheElement()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<PersonalDataFormatAggregate>(es => es.WithEvent<PersonalDataFormatEvent>());

        var integrity = EventIntegrityChain.Seal([1, 2, 3], new Byte[32], IntegritySealMode.Append);

        integrity.ToBsonDocument().Names.Should().BeEquivalentTo("FormatVersion", "Algorithm", "PreviousHash", "Hash", "SealMode");
    }

    [Test]
    public void PersonalDataCommitmentClassMap_PinsElementNamesAndOmitsNullSalt()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<PersonalDataCommitmentMapAggregate>(es => es
                                                                                                         .WithEvent<PersonalDataCommitmentMapEvent>());
        var classMap = BsonClassMap.LookupClassMap(typeof(PersonalDataCommitment));

        var committed = new PersonalDataCommitment
        {
            Path = "Name",
            Salt = new Byte[32],
            Commitment = new Byte[32]
        }.ToBsonDocument();
        var erased = new PersonalDataCommitment
        {
            Path = "Name",
            Commitment = new Byte[32]
        }.ToBsonDocument();

        classMap.IgnoreExtraElements.Should().BeTrue();
        committed.Names.Should().BeEquivalentTo("Path", "Salt", "Commitment");
        committed["Salt"].AsBsonBinaryData.SubType.Should().Be(BsonBinarySubType.Binary);
        erased.Names.Should().BeEquivalentTo("Path", "Commitment");
    }

    [Test]
    public void ResolveEventType_RegisteredDiscriminators_ResolvesConcreteType()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<PersonalDataResolveAggregate>(es => es
                                                                                                   .WithEvent<PersonalDataResolveEvent>("PersonalDataResolved"));

        EventDocumentFields<PersonalDataResolveAggregate>.Discriminator.Should().Be("_t");
        EventDocumentFields<PersonalDataResolveAggregate>.ResolveEventType(new BsonArray
        {
            "Event`1",
            "PersonalDataResolved"
        }).Should().Be(typeof(PersonalDataResolveEvent));
        EventDocumentFields<PersonalDataResolveAggregate>.ResolveEventType("PersonalDataResolved").Should().Be(typeof(PersonalDataResolveEvent));
        EventDocumentFields<PersonalDataResolveAggregate>.ResolveEventType("PersonalDataUnknown").Should().BeNull();
        EventDocumentFields<PersonalDataResolveAggregate>.ResolveEventType(new BsonArray()).Should().BeNull();
    }

    [Test]
    public void WithEventStore_ArrayBeforeLastSegment_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataArrayAggregate>(es => es
                                                                               .WithEvent<PersonalDataArrayEvent>(null, e => e.WithPersonalData(x => x.People.Count)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataArrayEvent*'People.Count'*People*serialized as an array*");
    }

    [Test]
    public void WithEventStore_CustomSerializerBeforeLastSegment_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataCustomSerializerAggregate>(es => es
                                                                                          .WithEvent<PersonalDataCustomSerializerEvent>(
                                                                                              null,
                                                                                              e => e.WithPersonalData(x => x.Person.Name)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataCustomSerializerEvent*'Person.Name'*Person*cannot resolve member element names*");
    }

    [Test]
    public void WithEventStore_DictionaryBeforeLastSegment_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataDictionaryAggregate>(es => es
                                                                                    .WithEvent<PersonalDataDictionaryEvent>(
                                                                                        null,
                                                                                        e => e.WithPersonalData(x => x.People.Count)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataDictionaryEvent*'People.Count'*People*serialized as a dictionary*");
    }

    [Test]
    public void WithEventStore_DollarPrefixedElementName_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataDollarAggregate>(es => es
                                                                                .WithEvent<PersonalDataDollarEvent>(
                                                                                    null,
                                                                                    e => e.WithPersonalData(x => x.Contact.Email)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataDollarEvent*'Contact.Email'*Email*'$email'*starts with a dollar sign*");
    }

    [Test]
    public void WithEventStore_DottedElementName_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataDottedAggregate>(es => es
                                                                                .WithEvent<PersonalDataDottedEvent>(null, e => e.WithPersonalData(x => x.Email)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataDottedEvent*'Email'*Email*'contact.email'*contains a dot*");
    }

    [Test]
    public void WithEventStore_DuplicateMarks_ResolveToOnePath()
    {
        var services = new ServiceCollection();

        new MongoBuilder(services).WithEventStore<PersonalDataDuplicateAggregate>(es => es
                                                                                      .WithEvent<PersonalDataDuplicateEvent>(
                                                                                          null,
                                                                                          e => e.WithPersonalData(x => x.Memo).WithPersonalData(x => x.Memo)));

        var options = services.BuildServiceProvider().GetRequiredService<MongoEventStoreOptions<PersonalDataDuplicateAggregate>>();
        options.PersonalDataPaths[typeof(PersonalDataDuplicateEvent)].Should().Equal("Memo");
    }

    [Test]
    public void WithEventStore_EventDerivedFromMarkedEventInRegisteredHierarchy_InheritsMarks()
    {
        BsonClassMap.RegisterClassMap<PersonalDataHierarchyDerivedEvent>(cm => cm.AutoMap());
        BsonClassMap.RegisterClassMap<PersonalDataHierarchyUnmarkedEvent>(cm => cm.AutoMap());
        var services = new ServiceCollection();

        new MongoBuilder(services).WithEventStore<PersonalDataHierarchyAggregate>(es => es
                                                                                        .WithEvent<PersonalDataHierarchyEvent>(null, e => e.WithPersonalData(x => x.Memo))
                                                                                        .WithEvent<PersonalDataHierarchyDerivedEvent>(
                                                                                            null,
                                                                                            e => e.WithPersonalData(x => x.Note))
                                                                                        .WithEvent<PersonalDataHierarchyUnmarkedEvent>());

        var options = services.BuildServiceProvider().GetRequiredService<MongoEventStoreOptions<PersonalDataHierarchyAggregate>>();
        options.PersonalDataPaths[typeof(PersonalDataHierarchyEvent)].Should().Equal("Memo");
        options.PersonalDataPaths[typeof(PersonalDataHierarchyDerivedEvent)].Should().Equal("Memo", "Note");
        options.PersonalDataPaths[typeof(PersonalDataHierarchyUnmarkedEvent)].Should().Equal("Memo");
    }

    [Test]
    public void WithEventStore_EventDerivedFromMarkedEvent_InheritsMarks()
    {
        var services = new ServiceCollection();

        // The event store maps a derived event along its class hierarchy, so the inherited Memo is stored.
        new MongoBuilder(services).WithEventStore<PersonalDataInheritedAggregate>(es => es
                                                                                        .WithEvent<PersonalDataInheritedEvent>(null, e => e.WithPersonalData(x => x.Memo))
                                                                                        .WithEvent<PersonalDataInheritedDerivedEvent>(
                                                                                            null,
                                                                                            e => e.WithPersonalData(x => x.Note))
                                                                                        .WithEvent<PersonalDataInheritedUnmarkedEvent>());

        var options = services.BuildServiceProvider().GetRequiredService<MongoEventStoreOptions<PersonalDataInheritedAggregate>>();
        options.PersonalDataPaths[typeof(PersonalDataInheritedEvent)].Should().Equal("Memo");
        options.PersonalDataPaths[typeof(PersonalDataInheritedDerivedEvent)].Should().Equal("Memo", "Note");
        options.PersonalDataPaths[typeof(PersonalDataInheritedUnmarkedEvent)].Should().Equal("Memo");
    }

    [Test]
    public void WithEventStore_InheritedMarkOverlapsOwnMark_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<PersonalDataInheritedOverlapDerivedEvent>(cm => cm.AutoMap());
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataInheritedOverlapAggregate>(es => es
                                                                                            .WithEvent<PersonalDataInheritedOverlapEvent>(
                                                                                                null,
                                                                                                e => e.WithPersonalData(x => x.Person))
                                                                                            .WithEvent<PersonalDataInheritedOverlapDerivedEvent>(
                                                                                                null,
                                                                                                e => e.WithPersonalData(x => x.Person!.Name)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataInheritedOverlapDerivedEvent*'Person.Name'*overlaps*'Person'*");
    }

    [Test]
    public void WithEventStore_MarkedMembers_ResolveToRenamedNestedElementPaths()
    {
        var services = new ServiceCollection();

        new MongoBuilder(services).WithEventStore<PersonalDataRenamedAggregate>(es => es
                                                                                      .WithEvent<PersonalDataRenamedEvent>("PersonalDataRenamed", e => e
                                                                                              .WithPersonalData(x => x.Memo)
                                                                                              .WithPersonalData(x => x.Recipient.Name)
                                                                                              .WithPersonalData(x => x.Recipient.Address.Street)
                                                                                              .WithPersonalData(x => x.Age)
                                                                                              .WithPersonalData(x => x.Note))
                                                                                      .WithEvent<PersonalDataUnmarkedEvent>());

        var options = services.BuildServiceProvider().GetRequiredService<MongoEventStoreOptions<PersonalDataRenamedAggregate>>();
        options.PersonalDataPaths.Should().ContainKey(typeof(PersonalDataRenamedEvent)).WhoseValue
               .Should().Equal("Memo", "recipient.full_name", "recipient.Address.street", "Age", "Note");
        options.PersonalDataPaths.Should().NotContainKey(typeof(PersonalDataUnmarkedEvent));
        options.EventTypes[typeof(PersonalDataRenamedEvent)].Should().Be("PersonalDataRenamed");
    }

    [Test]
    public void WithEventStore_NonNullableReferenceMember_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataNonNullableAggregate>(es => es
                                                                                     .WithEvent<PersonalDataNonNullableEvent>(null, e => e.WithPersonalData(x => x.Name)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*PersonalDataNonNullableEvent*'Name'*Name*non-nullable*");
    }

    [Test]
    public void WithEventStore_NonNullableValueMember_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataValueTypeAggregate>(es => es
                                                                                   .WithEvent<PersonalDataValueTypeEvent>(null, e => e.WithPersonalData(x => x.Age)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*PersonalDataValueTypeEvent*'Age'*Age*Int32*cannot hold null*");
    }

    [Test]
    public void WithEventStore_OverlappingPaths_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        // The null-forgiving operator is erased from the expression tree; the nullable member is what makes the overlap reachable.
        var act = () => builder.WithEventStore<PersonalDataOverlapAggregate>(es => es
                                                                                 .WithEvent<PersonalDataOverlapEvent>(null, e => e
                                                                                                                          .WithPersonalData(x => x.Person!.Name)
                                                                                                                          .WithPersonalData(x => x.Person)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataOverlapEvent*'Person.Name'*'Person.Name'*overlaps*'Person'*'Person'*");
    }

    [Test]
    public void WithEventStore_PreRegisteredMapSkippingMarkedBase_SkipsInheritedMarksOfUnmappedMembers()
    {
        // The derived class map is built directly on the event base class map, so the inherited Memo is not stored.
        var derivedClassMap = new BsonClassMap(typeof(PersonalDataSkippedDerivedEvent),
                                               BsonClassMap.LookupClassMap(typeof(Event<PersonalDataSkippedAggregate>)));
        derivedClassMap.AutoMap();
        BsonClassMap.RegisterClassMap(derivedClassMap);
        var services = new ServiceCollection();

        new MongoBuilder(services).WithEventStore<PersonalDataSkippedAggregate>(es => es
                                                                                      .WithEvent<PersonalDataSkippedEvent>(null, e => e.WithPersonalData(x => x.Memo))
                                                                                      .WithEvent<PersonalDataSkippedDerivedEvent>(
                                                                                          null,
                                                                                          e => e.WithPersonalData(x => x.Note)));

        var options = services.BuildServiceProvider().GetRequiredService<MongoEventStoreOptions<PersonalDataSkippedAggregate>>();
        options.PersonalDataPaths[typeof(PersonalDataSkippedEvent)].Should().Equal("Memo");
        options.PersonalDataPaths[typeof(PersonalDataSkippedDerivedEvent)].Should().Equal("Note");
    }

    [Test]
    public void WithEventStore_PrimitiveBeforeLastSegment_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataPrimitiveAggregate>(es => es
                                                                                   .WithEvent<PersonalDataPrimitiveEvent>(null, e => e.WithPersonalData(x => x.Name!.Length)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PersonalDataPrimitiveEvent*'Name.Length'*Name*cannot resolve member element names*");
    }

    [Test]
    public void WithEventStore_UnmappedMember_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataUnmappedAggregate>(es => es
                                                                                  .WithEvent<PersonalDataUnmappedEvent>(null, e => e.WithPersonalData(x => x.Secret)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*PersonalDataUnmappedEvent*'Secret'*Secret*not mapped*");
    }

    [Test]
    public void WithEventStore_WithoutIntegrityProtection_StillValidatesMarks()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<PersonalDataUnprotectedAggregate>(es => es
                                                                                     .WithEvent<PersonalDataUnprotectedEvent>(null, e => e.WithPersonalData(x => x.Name)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*PersonalDataUnprotectedEvent*non-nullable*");
    }

    [Test]
    public void WithPersonalData_NotAMemberAccessChain_ThrowsArgumentException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var actMethodCall = () => builder.WithEventStore<PersonalDataExpressionAggregate>(es => es
                                                                                              .WithEvent<PersonalDataExpressionEvent>(
                                                                                                  null,
                                                                                                  e => e.WithPersonalData(x => x.Name!.ToUpperInvariant())));
        var actConstant = () => builder.WithEventStore<PersonalDataExpressionAggregate>(es => es
                                                                                            .WithEvent<PersonalDataExpressionEvent>(
                                                                                                null,
                                                                                                e => e.WithPersonalData(x => "constant")));
        var actParameter = () => builder.WithEventStore<PersonalDataExpressionAggregate>(es => es
                                                                                             .WithEvent<PersonalDataExpressionEvent>(
                                                                                                 null,
                                                                                                 e => e.WithPersonalData(x => x)));
        var actNull = () => builder.WithEventStore<PersonalDataExpressionAggregate>(es => es
                                                                                        .WithEvent<PersonalDataExpressionEvent>(
                                                                                            null,
                                                                                            e => e.WithPersonalData<String>(null!)));

        actMethodCall.Should().Throw<ArgumentException>().WithParameterName("member").WithMessage("*PersonalDataExpressionEvent*member-access chain*");
        actConstant.Should().Throw<ArgumentException>().WithParameterName("member");
        actParameter.Should().Throw<ArgumentException>().WithParameterName("member");
        actNull.Should().Throw<ArgumentNullException>().WithParameterName("member");
    }
}

public sealed class PersonalDataFormatAggregate : Aggregate;

public sealed class PersonalDataFormatEvent : Event<PersonalDataFormatAggregate>
{
    public override void Execute(PersonalDataFormatAggregate aggregate) { }
}

public sealed class PersonalDataCommitmentMapAggregate : Aggregate;

public sealed class PersonalDataCommitmentMapEvent : Event<PersonalDataCommitmentMapAggregate>
{
    public override void Execute(PersonalDataCommitmentMapAggregate aggregate) { }
}

public sealed class PersonalDataResolveAggregate : Aggregate;

public sealed class PersonalDataResolveEvent : Event<PersonalDataResolveAggregate>
{
    public override void Execute(PersonalDataResolveAggregate aggregate) { }
}

public sealed class PersonalDataPerson
{
    public PersonalDataAddress Address { get; set; } = new();
    public String? Name { get; set; }
}

public sealed class PersonalDataAddress
{
    [BsonElement("street")]
    public String? Street { get; set; }
}

public sealed class PersonalDataInheritedAggregate : Aggregate;

public class PersonalDataInheritedEvent : Event<PersonalDataInheritedAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(PersonalDataInheritedAggregate aggregate) { }
}

public sealed class PersonalDataInheritedDerivedEvent : PersonalDataInheritedEvent
{
    public String? Note { get; set; }
}

public sealed class PersonalDataInheritedUnmarkedEvent : PersonalDataInheritedEvent;

public sealed class PersonalDataSkippedAggregate : Aggregate;

public class PersonalDataSkippedEvent : Event<PersonalDataSkippedAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(PersonalDataSkippedAggregate aggregate) { }
}

public sealed class PersonalDataSkippedDerivedEvent : PersonalDataSkippedEvent
{
    public String? Note { get; set; }
}

public sealed class PersonalDataHierarchyAggregate : Aggregate;

public class PersonalDataHierarchyEvent : Event<PersonalDataHierarchyAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(PersonalDataHierarchyAggregate aggregate) { }
}

public sealed class PersonalDataHierarchyDerivedEvent : PersonalDataHierarchyEvent
{
    public String? Note { get; set; }
}

public sealed class PersonalDataHierarchyUnmarkedEvent : PersonalDataHierarchyEvent;

public sealed class PersonalDataInheritedOverlapAggregate : Aggregate;

public class PersonalDataInheritedOverlapEvent : Event<PersonalDataInheritedOverlapAggregate>
{
    public PersonalDataPerson? Person { get; set; }

    public override void Execute(PersonalDataInheritedOverlapAggregate aggregate) { }
}

public sealed class PersonalDataInheritedOverlapDerivedEvent : PersonalDataInheritedOverlapEvent;

public sealed class PersonalDataArrayAggregate : Aggregate;

public sealed class PersonalDataArrayEvent : Event<PersonalDataArrayAggregate>
{
    public List<PersonalDataPerson> People { get; set; } = [];

    public override void Execute(PersonalDataArrayAggregate aggregate) { }
}

public sealed class PersonalDataPersonStringSerializer : SerializerBase<PersonalDataPerson>
{
    public override PersonalDataPerson Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => new()
        {
            Name = context.Reader.ReadString()
        };

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, PersonalDataPerson value)
        => context.Writer.WriteString(value.Name ?? String.Empty);
}

public sealed class PersonalDataCustomSerializerAggregate : Aggregate;

public sealed class PersonalDataCustomSerializerEvent : Event<PersonalDataCustomSerializerAggregate>
{
    [BsonSerializer(typeof(PersonalDataPersonStringSerializer))]
    public PersonalDataPerson Person { get; set; } = new();

    public override void Execute(PersonalDataCustomSerializerAggregate aggregate) { }
}

public sealed class PersonalDataDictionaryAggregate : Aggregate;

public sealed class PersonalDataDictionaryEvent : Event<PersonalDataDictionaryAggregate>
{
    public Dictionary<String, PersonalDataPerson> People { get; set; } = new();

    public override void Execute(PersonalDataDictionaryAggregate aggregate) { }
}

public sealed class PersonalDataDuplicateAggregate : Aggregate;

public sealed class PersonalDataDuplicateEvent : Event<PersonalDataDuplicateAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(PersonalDataDuplicateAggregate aggregate) { }
}

public sealed class PersonalDataRenamedAggregate : Aggregate;

public sealed class PersonalDataRecipient
{
    public PersonalDataAddress Address { get; set; } = new();

    [BsonElement("full_name")]
    public String? Name { get; set; }
}

public sealed class PersonalDataRenamedEvent : Event<PersonalDataRenamedAggregate>
{
    public String? Note;

    public Int32? Age { get; set; }
    public String? Memo { get; set; }

    [BsonElement("recipient")]
    public PersonalDataRecipient Recipient { get; set; } = new();

    public override void Execute(PersonalDataRenamedAggregate aggregate) { }
}

public sealed class PersonalDataUnmarkedEvent : Event<PersonalDataRenamedAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(PersonalDataRenamedAggregate aggregate) { }
}

public sealed class PersonalDataNonNullableAggregate : Aggregate;

public sealed class PersonalDataNonNullableEvent : Event<PersonalDataNonNullableAggregate>
{
    public String Name { get; set; } = String.Empty;

    public override void Execute(PersonalDataNonNullableAggregate aggregate) { }
}

public sealed class PersonalDataValueTypeAggregate : Aggregate;

public sealed class PersonalDataValueTypeEvent : Event<PersonalDataValueTypeAggregate>
{
    public Int32 Age { get; set; }

    public override void Execute(PersonalDataValueTypeAggregate aggregate) { }
}

public sealed class PersonalDataOverlapAggregate : Aggregate;

public sealed class PersonalDataOverlapEvent : Event<PersonalDataOverlapAggregate>
{
    public PersonalDataPerson? Person { get; set; }

    public override void Execute(PersonalDataOverlapAggregate aggregate) { }
}

public sealed class PersonalDataPrimitiveAggregate : Aggregate;

public sealed class PersonalDataPrimitiveEvent : Event<PersonalDataPrimitiveAggregate>
{
    public String? Name { get; set; }

    public override void Execute(PersonalDataPrimitiveAggregate aggregate) { }
}

public sealed class PersonalDataDollarAggregate : Aggregate;

public sealed class PersonalDataDollarContact
{
    [BsonElement("$email")]
    public String? Email { get; set; }
}

public sealed class PersonalDataDollarEvent : Event<PersonalDataDollarAggregate>
{
    public PersonalDataDollarContact Contact { get; set; } = new();

    public override void Execute(PersonalDataDollarAggregate aggregate) { }
}

public sealed class PersonalDataDottedAggregate : Aggregate;

public sealed class PersonalDataDottedEvent : Event<PersonalDataDottedAggregate>
{
    [BsonElement("contact.email")]
    public String? Email { get; set; }

    public override void Execute(PersonalDataDottedAggregate aggregate) { }
}

public sealed class PersonalDataUnmappedAggregate : Aggregate;

public sealed class PersonalDataUnmappedEvent : Event<PersonalDataUnmappedAggregate>
{
    [BsonIgnore]
    public String? Secret { get; set; }

    public override void Execute(PersonalDataUnmappedAggregate aggregate) { }
}

public sealed class PersonalDataUnprotectedAggregate : Aggregate;

public sealed class PersonalDataUnprotectedEvent : Event<PersonalDataUnprotectedAggregate>
{
    public String Name { get; set; } = String.Empty;

    public override void Execute(PersonalDataUnprotectedAggregate aggregate) { }
}

public sealed class PersonalDataExpressionAggregate : Aggregate;

public sealed class PersonalDataExpressionEvent : Event<PersonalDataExpressionAggregate>
{
    public String? Name { get; set; }

    public override void Execute(PersonalDataExpressionAggregate aggregate) { }
}
