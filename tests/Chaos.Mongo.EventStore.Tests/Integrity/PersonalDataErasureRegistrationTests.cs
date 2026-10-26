// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using NUnit.Framework;
using System.Reflection;

/// <remarks>
/// Class-map registration is process-global and irreversible, so every test uses dedicated
/// aggregate and event types that no other test registers.
/// </remarks>
public class PersonalDataErasureRegistrationTests
{
    [Test]
    public void ErasureEntryClassMapMatches_OtherElementName_ReturnsFalse()
    {
        // The entry type is shared by all aggregates, so its class maps are checked without registering them.
        var standard = new BsonClassMap<PersonalDataErasureEntry>(cm => cm.AutoMap());
        var renamed = new BsonClassMap<PersonalDataErasureEntry>(cm =>
        {
            cm.AutoMap();
            cm.GetMemberMap(e => e.Paths).SetElementName("ErasedPaths");
        });

        MongoEventStoreSerializationSetup.ErasureEntryClassMapMatches(standard).Should().BeTrue();
        MongoEventStoreSerializationSetup.ErasureEntryClassMapMatches(renamed).Should().BeFalse();
    }

    [Test]
    public void ErasureRequest_CallerCollectionsChangedLater_KeepsTheValidatedSelection()
    {
        var versions = new List<Int64>
        {
            2
        };
        var paths = new List<String>
        {
            "Memo"
        };
        var request = new ErasureRequest("ticket", versions, paths);

        versions.Clear();
        paths.Add(" ");

        request.Versions.Should().Equal(2);
        request.Paths.Should().Equal("Memo");
    }

    [Test]
    public void ErasureRequest_EmptyCollections_ThrowArgumentException()
    {
        var actVersions = () => new ErasureRequest("ticket", []);
        var actPaths = () => new ErasureRequest("ticket", paths: []);

        actVersions.Should().Throw<ArgumentException>().WithParameterName("versions");
        actPaths.Should().Throw<ArgumentException>().WithParameterName("paths");
    }

    [Test]
    public void ErasureRequest_PathWithWhitespace_ThrowsArgumentException()
    {
        var act = () => new ErasureRequest("ticket", paths: ["Memo", " "]);

        act.Should().Throw<ArgumentException>().WithParameterName("paths");
    }

    [Test]
    public void ErasureRequest_ValidArguments_ExposesSelection()
    {
        var request = new ErasureRequest("ticket", [2, 3], ["Memo"]);

        request.Reference.Should().Be("ticket");
        request.Versions.Should().Equal(2, 3);
        request.Paths.Should().Equal("Memo");
        new ErasureRequest("ticket").Versions.Should().BeNull();
        new ErasureRequest("ticket").Paths.Should().BeNull();
    }

    [Test]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ErasureRequest_WhitespaceReference_ThrowsArgumentException(String? reference)
    {
        var act = () => new ErasureRequest(reference!);

        act.Should().Throw<ArgumentException>().WithParameterName("reference");
    }

    [Test]
    public void IsErasureEvent_StoredDiscriminators_MatchesOnlyTheReservedConcreteDiscriminator()
    {
        const String reserved = PersonalDataErased<ErasureRegistrationAggregate>.Discriminator;

        EventDocumentFields<ErasureRegistrationAggregate>.IsErasureEvent(reserved).Should().BeTrue();
        EventDocumentFields<ErasureRegistrationAggregate>.IsErasureEvent(new BsonArray
        {
            "Event",
            reserved
        }).Should().BeTrue();
        EventDocumentFields<ErasureRegistrationAggregate>.IsErasureEvent(new BsonArray
        {
            reserved,
            "Other"
        }).Should().BeFalse();
        EventDocumentFields<ErasureRegistrationAggregate>.IsErasureEvent("Other").Should().BeFalse();
        EventDocumentFields<ErasureRegistrationAggregate>.IsErasureEvent(new BsonArray()).Should().BeFalse();
        EventDocumentFields<ErasureRegistrationAggregate>.IsErasureEvent(BsonNull.Value).Should().BeFalse();
    }

    [Test]
    public void StreamVerificationResult_Equality_ComparesUnrecordedErasuresByValue()
    {
        var erasure = new UnrecordedErasure(2, "Memo");

        StreamVerificationResult.IntactWith([]).Should().BeSameAs(StreamVerificationResult.Intact);
        StreamVerificationResult.IntactWith([erasure]).Should().Be(new StreamVerificationResult(null, null, [new UnrecordedErasure(2, "Memo")]));
        StreamVerificationResult.IntactWith([erasure]).GetHashCode().Should().Be(new StreamVerificationResult(null, null, [erasure]).GetHashCode());
        StreamVerificationResult.IntactWith([erasure]).Should().NotBe(StreamVerificationResult.Intact);
        StreamVerificationResult.IntactWith([erasure]).IsIntact.Should().BeTrue();
        StreamVerificationResult.Broken(2, StreamVerificationFailure.HashMismatch)
                                .Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.HashMismatch));
        StreamVerificationResult.Broken(2, StreamVerificationFailure.HashMismatch).UnrecordedErasures.Should().BeEmpty();
    }

    [Test]
    public void WithEventStore_ConventionMappingNonPublicMembers_NeverMapsTheErasersMarker()
    {
        var pack = new ConventionPack
        {
            new MapNonPublicPropertiesConvention()
        };
        ConventionRegistry.Register(nameof(WithEventStore_ConventionMappingNonPublicMembers_NeverMapsTheErasersMarker),
                                    pack,
                                    type => type == typeof(PersonalDataErased<ErasureConventionAggregate>));

        new MongoBuilder(new ServiceCollection()).WithEventStore<ErasureConventionAggregate>(es => es.WithEvent<ErasureConventionEvent>());
        var erased = new PersonalDataErased<ErasureConventionAggregate>
        {
            Reference = "ticket",
            CreatedByEraser = true
        };
        var document = erased.ToBsonDocument(typeof(Event<ErasureConventionAggregate>));
        var roundTrip = BsonSerializer.Deserialize<Event<ErasureConventionAggregate>>(document);

        BsonClassMap.LookupClassMap(typeof(PersonalDataErased<ErasureConventionAggregate>))
                    .GetMemberMap(nameof(PersonalDataErased<ErasureConventionAggregate>.CreatedByEraser)).Should().BeNull();
        document.Names.Should().NotContain(nameof(PersonalDataErased<ErasureConventionAggregate>.CreatedByEraser));
        roundTrip.Should().BeOfType<PersonalDataErased<ErasureConventionAggregate>>().Which.CreatedByEraser.Should().BeFalse();
    }

    [Test]
    public void WithEventStore_PreRegisteredClassMapWithReservedDiscriminator_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<ErasurePreRegisteredEvent>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator(PersonalDataErased<ErasurePreRegisteredAggregate>.Discriminator);
        });
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<ErasurePreRegisteredAggregate>(es => es.WithEvent<ErasurePreRegisteredEvent>("Harmless"));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*ErasurePreRegisteredEvent*registered before the event store*" +
                        $"'{PersonalDataErased<ErasurePreRegisteredAggregate>.Discriminator}'*reserved*");
    }

    [Test]
    public void WithEventStore_PreRegisteredErasureClassMapMappingTheMarker_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<PersonalDataErased<ErasureMarkerMappedAggregate>>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator(PersonalDataErased<ErasureMarkerMappedAggregate>.Discriminator);
            cm.MapMember(e => e.CreatedByEraser);
        });

        var act = () => new MongoBuilder(new ServiceCollection()).WithEventStore<ErasureMarkerMappedAggregate>(es => es.WithEvent<ErasureMarkerMappedEvent>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*PersonalDataErased*eraser's marker*");
        BsonClassMap.IsClassMapRegistered(typeof(ErasureMarkerMappedEvent)).Should().BeFalse("the check runs before anything is registered");
    }

    [Test]
    public void WithEventStore_PreRegisteredErasureClassMapWithOtherElementName_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<PersonalDataErased<ErasureRenamedAggregate>>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator(PersonalDataErased<ErasureRenamedAggregate>.Discriminator);
            cm.GetMemberMap(e => e.Reference).SetElementName("Ticket");
        });

        var act = () => new MongoBuilder(new ServiceCollection()).WithEventStore<ErasureRenamedAggregate>(es => es.WithEvent<ErasureRenamedEvent>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*PersonalDataErased*registered before the event store*");
        BsonClassMap.IsClassMapRegistered(typeof(ErasureRenamedAggregate)).Should().BeFalse("the check runs before anything is registered");
        BsonClassMap.IsClassMapRegistered(typeof(ErasureRenamedEvent)).Should().BeFalse();
    }

    [Test]
    public void WithEventStore_ReservedDiscriminator_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<ErasureReservedAggregate>(es => es
                                                                             .WithEvent<ErasureReservedEvent>(PersonalDataErased<ErasureReservedAggregate>.Discriminator));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*ErasureReservedEvent*'{PersonalDataErased<ErasureReservedAggregate>.Discriminator}'*reserved*");
    }

    [Test]
    public void WithEventStore_WithProtection_RegistersScopedEraser()
    {
        var services = new ServiceCollection();

        new MongoBuilder(services).WithEventStore<ErasureProtectedAggregate>(es => es.WithEvent<ErasureProtectedEvent>().WithIntegrityProtection());

        services.Should().ContainSingle(d => d.ServiceType == typeof(PersonalDataEraser<ErasureProtectedAggregate>))
                .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Test]
    public void WithEventStore_WithoutProtection_RegistersErasureEventWithoutEraser()
    {
        var services = new ServiceCollection();

        new MongoBuilder(services).WithEventStore<ErasureRegistrationAggregate>(es => es
                                                                                      .WithEvent<ErasureRegistrationEvent>()
                                                                                      .WithEvent<ErasureRegistrationAuditEvent>("Audited"));

        var options = services.BuildServiceProvider().GetRequiredService<MongoEventStoreOptions<ErasureRegistrationAggregate>>();
        var classMap = BsonClassMap.LookupClassMap(typeof(PersonalDataErased<ErasureRegistrationAggregate>));
        var erased = new PersonalDataErased<ErasureRegistrationAggregate>
        {
            Reference = "ticket",
            Entries =
            [
                new PersonalDataErasureEntry
                {
                    Version = 1,
                    Paths = ["Memo", "Recipient.Name"]
                }
            ]
        };
        var document = erased.ToBsonDocument(typeof(Event<ErasureRegistrationAggregate>));
        var roundTrip = BsonSerializer.Deserialize<Event<ErasureRegistrationAggregate>>(document);

        classMap.Discriminator.Should().Be(PersonalDataErased<ErasureRegistrationAggregate>.Discriminator);
        classMap.DeclaredMemberMaps.Select(m => m.ElementName).Should().BeEquivalentTo("Reference", "Entries");
        options.ObservationalEventDiscriminators.Should().BeEquivalentTo("Audited", PersonalDataErased<ErasureRegistrationAggregate>.Discriminator);
        services.Should().NotContain(d => d.ServiceType == typeof(PersonalDataEraser<ErasureRegistrationAggregate>));
        document["_t"].AsBsonArray.Last().AsString.Should().Be(PersonalDataErased<ErasureRegistrationAggregate>.Discriminator);
        document["Reference"].AsString.Should().Be("ticket");
        document["Entries"][0]["Version"].AsInt64.Should().Be(1);
        document["Entries"][0]["Paths"].AsBsonArray.Select(p => p.AsString).Should().Equal("Memo", "Recipient.Name");
        roundTrip.Should().BeOfType<PersonalDataErased<ErasureRegistrationAggregate>>()
                 .Which.Entries.Should().ContainSingle()
                 .Which.Paths.Should().Equal("Memo", "Recipient.Name");
    }
}

public sealed class ErasureRegistrationAggregate : Aggregate;

public sealed class ErasureRegistrationEvent : Event<ErasureRegistrationAggregate>
{
    public override void Execute(ErasureRegistrationAggregate aggregate) { }
}

public sealed class ErasureRegistrationAuditEvent : ObservationalEvent<ErasureRegistrationAggregate>;

public sealed class ErasureProtectedAggregate : Aggregate;

public sealed class ErasureProtectedEvent : Event<ErasureProtectedAggregate>
{
    public override void Execute(ErasureProtectedAggregate aggregate) { }
}

public sealed class ErasureMarkerMappedAggregate : Aggregate;

public sealed class ErasureMarkerMappedEvent : Event<ErasureMarkerMappedAggregate>
{
    public override void Execute(ErasureMarkerMappedAggregate aggregate) { }
}

public sealed class ErasurePreRegisteredAggregate : Aggregate;

public sealed class ErasurePreRegisteredEvent : Event<ErasurePreRegisteredAggregate>
{
    public override void Execute(ErasurePreRegisteredAggregate aggregate) { }
}

public sealed class ErasureConventionAggregate : Aggregate;

public sealed class ErasureConventionEvent : Event<ErasureConventionAggregate>
{
    public override void Execute(ErasureConventionAggregate aggregate) { }
}

public sealed class ErasureRenamedAggregate : Aggregate;

public sealed class ErasureRenamedEvent : Event<ErasureRenamedAggregate>
{
    public override void Execute(ErasureRenamedAggregate aggregate) { }
}

/// <summary>
/// A consumer convention that maps the non-public properties of a class.
/// </summary>
public sealed class MapNonPublicPropertiesConvention : ConventionBase, IClassMapConvention
{
    public void Apply(BsonClassMap classMap)
    {
        foreach (var property in classMap.ClassType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            classMap.MapProperty(property.Name);
        }
    }
}

public sealed class ErasureReservedAggregate : Aggregate;

public sealed class ErasureReservedEvent : Event<ErasureReservedAggregate>
{
    public override void Execute(ErasureReservedAggregate aggregate) { }
}
