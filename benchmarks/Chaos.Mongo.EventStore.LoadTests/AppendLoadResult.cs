// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

internal sealed record AppendLoadResult(Double Throughput, Double P50, Double P99, Int32 Appends);
