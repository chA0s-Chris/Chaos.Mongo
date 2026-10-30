// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

/// <summary>
/// The MongoDB server images the test and benchmark suites run against. The CI matrix selects one of the
/// named constants through the <c>CHAOSMONGO_MONGODB_IMAGE</c> environment variable; without it, tests use
/// <see cref="Primary"/>.
/// </summary>
public static class MongoDbTestImages
{
    // renovate: datasource=docker depName=mongo-latest packageName=mongo versioning=docker
    public const String Latest = "mongo:9.0.4";

    // renovate: datasource=docker depName=mongo-minimum-supported packageName=mongo versioning=docker
    public const String MinimumSupported = "mongo:7.0.45";

    // renovate: datasource=docker depName=mongo-primary packageName=mongo versioning=docker
    public const String Primary = "mongo:8.0.34";

    /// <summary>
    /// The image named by <c>CHAOSMONGO_MONGODB_IMAGE</c>, defaulting to <see cref="Primary"/> when the
    /// variable is unset or does not name one of the three constants.
    /// </summary>
    public static String Selected
        => Environment.GetEnvironmentVariable("CHAOSMONGO_MONGODB_IMAGE") switch
        {
            nameof(MinimumSupported) => MinimumSupported,
            nameof(Primary) => Primary,
            nameof(Latest) => Latest,
            _ => Primary
        };

    /// <summary>
    /// Whether <see cref="Selected"/> supports EventStore bulk-write optimization, matching the production
    /// capability guard in <c>MongoEventStoreBulkWriteSupport</c> (MongoDB 8.0 or later).
    /// </summary>
    public static Boolean SupportsBulkWriteOptimization
        => Int32.Parse(Selected.Split(':')[1].Split('.')[0]) >= 8;
}
