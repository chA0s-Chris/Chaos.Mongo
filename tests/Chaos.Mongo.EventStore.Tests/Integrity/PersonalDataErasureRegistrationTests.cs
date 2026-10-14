// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

/// <remarks>
/// Class-map registration is process-global and irreversible, so every test uses dedicated
/// aggregate and event types that no other test registers.
/// </remarks>
public class PersonalDataErasureRegistrationTests
{
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

public sealed class ErasurePreRegisteredAggregate : Aggregate;

public sealed class ErasurePreRegisteredEvent : Event<ErasurePreRegisteredAggregate>
{
    public override void Execute(ErasurePreRegisteredAggregate aggregate) { }
}

public sealed class ErasureReservedAggregate : Aggregate;

public sealed class ErasureReservedEvent : Event<ErasureReservedAggregate>
{
    public override void Execute(ErasureReservedAggregate aggregate) { }
}
