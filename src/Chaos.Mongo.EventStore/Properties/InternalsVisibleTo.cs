// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
#if !NUGET_RELEASE
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Chaos.Mongo.EventStore.Tests")]
[assembly: InternalsVisibleTo("Chaos.Mongo.EventStore.Benchmarks")]
#endif
