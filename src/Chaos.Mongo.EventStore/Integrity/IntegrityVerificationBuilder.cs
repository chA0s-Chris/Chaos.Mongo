// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Configures how anchors and their timestamp tokens are verified, including in a container that does not anchor.
/// </summary>
internal sealed class IntegrityVerificationBuilder
{
    private readonly IntegrityAnchoringOptions _anchoring;
    private String[]? _anchorCollections;

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
    public IntegrityVerificationOptions Options { get; }

    /// <summary>
    /// Sets the names of the collections that hold the anchors, their leaves and their evidence, when they differ from
    /// the defaults. In a container that also anchors, the anchoring configuration must use the same names; the
    /// registration fails otherwise, so verification cannot redirect where anchors are written.
    /// </summary>
    /// <param name="anchors">The anchors collection.</param>
    /// <param name="leaves">The anchor leaves collection.</param>
    /// <param name="evidence">The evidence collection.</param>
    /// <param name="evidenceChunks">The evidence chunks collection.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when a name is empty.</exception>
    public IntegrityVerificationBuilder WithAnchorCollections(String anchors, String leaves, String evidence, String evidenceChunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchors);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaves);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceChunks);
        _anchorCollections = [anchors, leaves, evidence, evidenceChunks];
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
    /// Sets the trust settings for the stored tokens of an authority. They take precedence over the settings of an RFC
    /// 3161 authority with the same name in the anchoring configuration; tokens of an authority without trust settings
    /// are not trusted.
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
    /// Applies the configured anchor collection names, when any, to the anchoring options shared with a later anchoring
    /// configuration.
    /// </summary>
    internal void ApplyAnchorCollections()
    {
        if (_anchorCollections is not [var anchors, var leaves, var evidence, var evidenceChunks])
        {
            return;
        }

        _anchoring.AnchorsCollectionName = anchors;
        _anchoring.AnchorLeavesCollectionName = leaves;
        _anchoring.AnchorEvidenceCollectionName = evidence;
        _anchoring.AnchorEvidenceChunksCollectionName = evidenceChunks;
    }

    /// <summary>
    /// Ensures that the configured anchor collection names, when any, are those of the anchoring configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the names differ.</exception>
    internal void EnsureAnchorCollectionsMatch()
    {
        String[] configured =
        [
            _anchoring.AnchorsCollectionName,
            _anchoring.AnchorLeavesCollectionName,
            _anchoring.AnchorEvidenceCollectionName,
            _anchoring.AnchorEvidenceChunksCollectionName
        ];
        if (_anchorCollections is { } names && !names.SequenceEqual(configured, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The anchor collections configured for integrity verification differ from those of integrity anchoring; " +
                                                "configure the same names for both.");
        }
    }
}
