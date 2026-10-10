// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

internal sealed class LoadTestAggregate : Aggregate
{
    public Decimal TotalAmount { get; set; }
}
