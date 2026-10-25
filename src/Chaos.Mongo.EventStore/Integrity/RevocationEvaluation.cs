// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of evaluating CRLs for the distribution points of a chain.
/// </summary>
/// <param name="Covered">For each source, in order, whether a CRL counts for it.</param>
/// <param name="Entries">The entries of every CRL that counts for a source.</param>
internal sealed record RevocationEvaluation(IReadOnlyList<Boolean> Covered, IReadOnlyList<RevokedCertificate> Entries);
