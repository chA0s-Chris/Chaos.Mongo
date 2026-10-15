// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Module-level settings for verifying anchors and their timestamp tokens.
/// </summary>
internal sealed class IntegrityVerificationOptions
{
    /// <summary>
    /// Gets the trust settings contributed by the RFC 3161 authorities of the anchoring configuration, by authority
    /// name. Explicitly configured trust settings take precedence.
    /// </summary>
    public Dictionary<String, TimestampAuthorityTrust> AnchoringAuthorities { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the explicitly configured trust settings, by authority name.
    /// </summary>
    public Dictionary<String, TimestampAuthorityTrust> Authorities { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the time after a token's time that the CRLs confirming its revocation status must be issued at or
    /// after, or <c>null</c> to use the anchoring grace period, which defaults to 24 hours.
    /// </summary>
    public TimeSpan? RevocationGracePeriod { get; set; }

    /// <summary>
    /// Finds the trust settings of an authority: the explicitly configured ones, or else those its anchoring
    /// configuration contributed.
    /// </summary>
    /// <param name="authority">The authority name stored on the anchor.</param>
    /// <returns>The trust settings, or <c>null</c> when none are configured.</returns>
    public TimestampAuthorityTrust? FindTrust(String authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return Authorities.GetValueOrDefault(authority) ?? AnchoringAuthorities.GetValueOrDefault(authority);
    }
}
