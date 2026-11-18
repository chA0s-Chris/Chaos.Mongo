// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using NUnit.Framework;

public class EventEvolutionTests
{
    [Test]
    public void Deserialize_AddedConstructorParameterIsOnlyOptionalInCSharp_Throws()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ConstructorMappedStockAggregate>(store => store.WithEvent<OptionalParameterStockReceivedEvent>());
        var historical = new OptionalParameterStockReceivedEvent("sku-1", 7).ToBsonDocument();
        historical.Remove(nameof(OptionalParameterStockReceivedEvent.BinNumber));

        var act = () => BsonSerializer.Deserialize<Event<ConstructorMappedStockAggregate>>(historical);

        act.Should().Throw<BsonSerializationException>().WithMessage("No matching creator found*");
    }

    [Test]
    public void Deserialize_AddedConstructorParameterWithConstructorForOldShape_UsesOldConstructor()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ConstructorMappedStockAggregate>(store => store.WithEvent<TwoConstructorStockReceivedEvent>());
        var historical = new TwoConstructorStockReceivedEvent("sku-1", 7).ToBsonDocument();
        historical.Remove(nameof(TwoConstructorStockReceivedEvent.BinNumber));

        var deserialized = BsonSerializer.Deserialize<Event<ConstructorMappedStockAggregate>>(historical);

        var received = deserialized.Should().BeOfType<TwoConstructorStockReceivedEvent>().Subject;
        received.Sku.Should().Be("sku-1");
        received.BinNumber.Should().Be(TwoConstructorStockReceivedEvent.UnrecordedBinNumber);
    }

    [Test]
    public void Deserialize_ConstructorMappedEventLacksAddedElements_AppliesMemberDefaults()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ConstructorMappedStockAggregate>(store => store.WithEvent<ConstructorMappedStockReceivedEvent>());
        var historical = new ConstructorMappedStockReceivedEvent("sku-1", 7)
        {
            Warehouse = "north"
        }.ToBsonDocument();
        historical.Remove(nameof(ConstructorMappedStockReceivedEvent.BinNumber));
        historical.Remove(nameof(ConstructorMappedStockReceivedEvent.Warehouse));

        var deserialized = BsonSerializer.Deserialize<Event<ConstructorMappedStockAggregate>>(historical);

        var received = deserialized.Should().BeOfType<ConstructorMappedStockReceivedEvent>().Subject;
        received.Sku.Should().Be("sku-1");
        received.BinNumber.Should().Be(42);
        received.Warehouse.Should().Be("not recorded");
    }
}

public sealed class ConstructorMappedStockAggregate : Aggregate;

public sealed class ConstructorMappedStockReceivedEvent : Event<ConstructorMappedStockAggregate>
{
    [BsonConstructor]
    public ConstructorMappedStockReceivedEvent(String sku, Int32 binNumber)
    {
        Sku = sku;
        BinNumber = binNumber;
    }

    [BsonDefaultValue(42)]
    public Int32 BinNumber { get; set; }

    public String Sku { get; set; }
    public String Warehouse { get; set; } = "not recorded";

    public override void Execute(ConstructorMappedStockAggregate aggregate) { }
}

public sealed class OptionalParameterStockReceivedEvent : Event<ConstructorMappedStockAggregate>
{
    [BsonConstructor]
    public OptionalParameterStockReceivedEvent(String sku, Int32 binNumber = 42)
    {
        Sku = sku;
        BinNumber = binNumber;
    }

    public Int32 BinNumber { get; set; }
    public String Sku { get; set; }

    public override void Execute(ConstructorMappedStockAggregate aggregate) { }
}

public sealed class TwoConstructorStockReceivedEvent : Event<ConstructorMappedStockAggregate>
{
    public const Int32 UnrecordedBinNumber = -1;

    [BsonConstructor]
    public TwoConstructorStockReceivedEvent(String sku)
    {
        Sku = sku;
        BinNumber = UnrecordedBinNumber;
    }

    [BsonConstructor]
    public TwoConstructorStockReceivedEvent(String sku, Int32 binNumber)
    {
        Sku = sku;
        BinNumber = binNumber;
    }

    public Int32 BinNumber { get; set; }
    public String Sku { get; set; }

    public override void Execute(ConstructorMappedStockAggregate aggregate) { }
}
