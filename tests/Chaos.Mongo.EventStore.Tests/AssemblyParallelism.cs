// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
using NUnit.Framework;

// Fixtures run in parallel; the tests of one fixture still run one after the other. Most of the suite waits on the
// shared MongoDB server, so more fixtures than the four vCPUs of a hosted runner keep it busy.
[assembly: Parallelizable(ParallelScope.Fixtures)]
[assembly: LevelOfParallelism(8)]
