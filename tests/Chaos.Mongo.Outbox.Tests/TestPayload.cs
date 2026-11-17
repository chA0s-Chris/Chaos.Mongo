// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

public class TestPayload
{
    public String Name { get; init; } = String.Empty;
    public Int32 Value { get; init; }
}

public class AnotherTestPayload
{
    public String Description { get; set; } = String.Empty;
}

public sealed record RecordTestPayload(String Name, Int32 Value, RecordTestPayloadDetail Detail);

public sealed record RecordTestPayloadDetail(String Code);
