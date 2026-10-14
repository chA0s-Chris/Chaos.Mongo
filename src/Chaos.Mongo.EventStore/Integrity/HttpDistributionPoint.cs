// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A CRL distribution point that names at least one HTTP or HTTPS location.
/// </summary>
/// <param name="Locations">The alternative HTTP and HTTPS locations of the point's CRL.</param>
/// <param name="CoversAllReasons">Whether the point's CRL covers every revocation reason.</param>
/// <param name="NamesCrlIssuer">
/// Whether the point names a CRL issuer, which RFC 5280 requires when the CRL is not issued by the certificate's issuer.
/// </param>
internal sealed record HttpDistributionPoint(IReadOnlyList<Uri> Locations, Boolean CoversAllReasons, Boolean NamesCrlIssuer);
