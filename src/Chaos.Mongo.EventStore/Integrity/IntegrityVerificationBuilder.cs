// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Configures how anchors and their timestamp tokens are verified, including in a container that does not anchor.
/// </summary>
public sealed class IntegrityVerificationBuilder
{
    private readonly IntegrityAnchoringOptions _anchoring;
    private String? _prefix;

    internal IntegrityVerificationBuilder(IntegrityVerificationOptions options, IntegrityAnchoringOptions anchoring)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(anchoring);
        Options = options;
        _anchoring = anchoring;
    }

    /// <summary>
    /// Gets the verification options this builder fills.
    /// </summary>
    internal IntegrityVerificationOptions Options { get; }

    /// <summary>
    /// Sets the prefix of the five module-level anchoring collection names, when it differs from the default. In a
    /// container that also anchors, the anchoring configuration must use the same prefix; the registration fails
    /// otherwise, so verification cannot redirect where anchors are written.
    /// </summary>
    /// <param name="prefix">The collection prefix.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="prefix"/> is null, empty or whitespace-only.</exception>
    public IntegrityVerificationBuilder WithCollectionPrefix(String prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix;
        return this;
    }

    /// <summary>
    /// Sets the time after a token's time that the CRLs confirming its revocation status must be issued at or after.
    /// Defaults to the anchoring grace period, which defaults to 24 hours.
    /// </summary>
    /// <param name="gracePeriod">The grace period.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the grace period is not positive.</exception>
    public IntegrityVerificationBuilder WithRevocationGracePeriod(TimeSpan gracePeriod)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gracePeriod, TimeSpan.Zero);
        Options.RevocationGracePeriod = gracePeriod;
        return this;
    }

    /// <summary>
    /// Sets the trust settings for the stored tokens of an authority. They replace the settings of an RFC 3161
    /// authority with the same name in the anchoring configuration rather than adding to them; tokens of an authority
    /// without trust settings are not trusted. In a container that also anchors, they apply on receipt as well: a token
    /// whose chain does not equal the chain built to one of these roots, or that is not issued under the required
    /// policy, counts as a failed request, so keep the roots of current tokens trusted when adding older ones.
    /// </summary>
    /// <param name="name">The authority name stored on the anchors.</param>
    /// <param name="configure">Configures the trusted roots and the required policy.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is empty or already configured.</exception>
    public IntegrityVerificationBuilder WithTimestampAuthorityTrust(String name, Action<TimestampAuthorityTrust>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trust = new TimestampAuthorityTrust();
        configure?.Invoke(trust);
        if (!Options.Authorities.TryAdd(name, trust))
        {
            throw new ArgumentException($"Trust settings for timestamp authority '{name}' are configured more than once.", nameof(name));
        }

        return this;
    }

    /// <summary>
    /// Applies the configured collection prefix, when any, to the anchoring options shared with a later anchoring
    /// configuration.
    /// </summary>
    internal void ApplyAnchorCollections()
    {
        if (_prefix is null)
        {
            return;
        }

        _anchoring.CollectionPrefix = _prefix;
    }

    /// <summary>
    /// Ensures that the configured collection prefix, when any, is that of the anchoring configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the prefixes differ.</exception>
    internal void EnsureAnchorCollectionsMatch()
    {
        if (_prefix is { } prefix && !String.Equals(prefix, _anchoring.CollectionPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The anchor collections configured for integrity verification differ from those of integrity anchoring; " +
                                                "configure the same prefix for both.");
        }
    }

    /// <summary>
    /// Restores the default collection prefix in the shared options before an anchoring configuration fills it, when a
    /// prefix was configured here, so the anchoring configuration must name the same collections itself.
    /// </summary>
    internal void RestoreDefaultAnchorCollections()
    {
        if (_prefix is null)
        {
            return;
        }

        _anchoring.CollectionPrefix = new IntegrityAnchoringOptions().CollectionPrefix;
    }
}
