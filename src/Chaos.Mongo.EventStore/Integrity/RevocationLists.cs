// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The CRLs that confirm the revocation status of a certificate chain at a time, or the information that a CRL
/// available today was issued too early to do so.
/// </summary>
public sealed class RevocationLists
{
    private RevocationLists(IReadOnlyList<Byte[]> lists, DateTime? nextUpdateUtc)
    {
        Lists = lists;
        NextUpdateUtc = nextUpdateUtc;
    }

    /// <summary>
    /// Gets a value indicating whether a distribution point offers only a CRL issued too early.
    /// </summary>
    public Boolean IsIssuedTooEarly => NextUpdateUtc is not null;

    /// <summary>
    /// Gets the CRLs, one per distribution point of the chain's non-root certificates; empty when the CRLs were issued
    /// too early or the chain names no distribution points.
    /// </summary>
    public IReadOnlyList<Byte[]> Lists { get; }

    /// <summary>
    /// Gets the earliest next update among the CRLs issued too early, or <c>null</c> when the CRLs are available.
    /// Its <see cref="DateTime.Kind"/> is <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    public DateTime? NextUpdateUtc { get; }

    /// <summary>
    /// Creates the result for CRLs that were all issued late enough.
    /// </summary>
    /// <param name="lists">The CRLs.</param>
    /// <returns>The result.</returns>
    public static RevocationLists Issued(IReadOnlyList<Byte[]> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        return new RevocationLists(lists, null);
    }

    /// <summary>
    /// Creates the result for a distribution point whose current CRL was issued too early.
    /// </summary>
    /// <param name="nextUpdateUtc">
    /// The earliest next update among the CRLs issued too early. Its <see cref="DateTime.Kind"/> must be
    /// <see cref="DateTimeKind.Utc"/>.
    /// </param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nextUpdateUtc"/>'s <see cref="DateTime.Kind"/> is not <see cref="DateTimeKind.Utc"/>.
    /// </exception>
    public static RevocationLists IssuedTooEarly(DateTime nextUpdateUtc)
    {
        if (nextUpdateUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException($"{nameof(nextUpdateUtc)} must have {nameof(DateTimeKind.Utc)} kind.", nameof(nextUpdateUtc));
        }

        return new RevocationLists([], nextUpdateUtc);
    }
}
