// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A CRL to capture: the alternative locations of a distribution point and the issuer its CRL must come from, or
/// <c>null</c> when the point names another CRL issuer.
/// </summary>
/// <param name="Locations">The point's HTTP and HTTPS locations.</param>
/// <param name="Issuer">The encoded name of the expected CRL issuer, or <c>null</c> for an indirect CRL issuer.</param>
/// <param name="FromCaCertificate">
/// Whether a CA certificate names the point, so a CRL limited to end-entity certificates does not cover it.
/// </param>
/// <param name="FromEndEntityCertificate">
/// Whether an end-entity certificate names the point, so a CRL limited to CA certificates does not cover it.
/// </param>
internal sealed record CrlSource(IReadOnlyList<Uri> Locations, Byte[]? Issuer, Boolean FromCaCertificate, Boolean FromEndEntityCertificate);
