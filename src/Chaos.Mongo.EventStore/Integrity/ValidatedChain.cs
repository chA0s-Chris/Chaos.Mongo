// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A validated certificate chain and the CRLs its non-root certificates name.
/// </summary>
/// <param name="Certificates">The DER-encoded certificates from the signing certificate to the root.</param>
/// <param name="CrlSources">The CRL sources, in capture order.</param>
internal sealed record ValidatedChain(IReadOnlyList<Byte[]> Certificates, IReadOnlyList<CrlSource> CrlSources);
