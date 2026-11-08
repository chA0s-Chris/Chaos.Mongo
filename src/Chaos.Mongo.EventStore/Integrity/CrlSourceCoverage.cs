// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The CRL sources a chain's non-root certificates name, and whether every one of them, or only some, named at least
/// one. A chain whose certificates all name none has nothing to confirm; a chain where some do and some do not can
/// never reach full confirmation, because the silent ones are never checked.
/// </summary>
/// <param name="Sources">The distinct CRL sources, in capture order.</param>
/// <param name="AnyCertificateHasSource">Whether at least one non-root certificate of the chain names a source.</param>
/// <param name="EveryCertificateHasSource">Whether every non-root certificate of the chain names at least one source.</param>
internal sealed record CrlSourceCoverage(IReadOnlyList<CrlSource> Sources, Boolean AnyCertificateHasSource, Boolean EveryCertificateHasSource);
