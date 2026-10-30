// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Configures the module-level anchoring pipeline. There is no default timestamp authority.
/// </summary>
public sealed class IntegrityAnchoringBuilder
{
    private readonly List<Func<IServiceProvider, ITimestampAuthority>> _authorities = [];
    private readonly HashSet<String> _authorityNames = [];
    private readonly List<Rfc3161TimestampAuthorityOptions> _rfc3161Authorities = [];

    internal IntegrityAnchoringBuilder(IntegrityAnchoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>
    /// Gets the completion hook, if one is configured.
    /// </summary>
    internal Func<IServiceProvider, CompletedAnchor, CancellationToken, Task>? CompletionHook { get; private set; }

    /// <summary>
    /// Gets the anchoring options this builder fills.
    /// </summary>
    internal IntegrityAnchoringOptions Options { get; }

    /// <summary>
    /// Gets the options of the configured RFC 3161 authorities, whose trust settings also apply to verification.
    /// </summary>
    internal IReadOnlyList<Rfc3161TimestampAuthorityOptions> Rfc3161Authorities => _rfc3161Authorities;

    /// <summary>
    /// Sets the hook that is notified of every completed anchor, in anchor order and at least once. Anchors completed
    /// before a hook was configured are notified first, in order.
    /// </summary>
    /// <remarks>
    /// An anchor completes long before the revocation grace period of its tokens has passed, so its timestamp entries
    /// are usually still unconfirmed when the hook receives it. The confirming CRLs are added to the stored anchor
    /// later; the hook is not notified again.
    /// </remarks>
    /// <param name="hook">The hook; it receives the service provider, the anchor and a cancellation token.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder OnAnchorCompleted(Func<IServiceProvider, CompletedAnchor, CancellationToken, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        CompletionHook = hook;
        return this;
    }

    /// <summary>
    /// Sets the prefix of the five module-level anchoring collection names. Defaults to <c>"Integrity"</c>.
    /// </summary>
    /// <param name="prefix">The collection prefix.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="prefix"/> is null, empty or whitespace-only.</exception>
    public IntegrityAnchoringBuilder WithCollectionPrefix(String prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Options.CollectionPrefix = prefix;
        return this;
    }

    /// <summary>
    /// Sets the age of the oldest unanchored candidate above which the anchor delay is reported. Defaults to 1 hour.
    /// </summary>
    /// <param name="threshold">The threshold.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="threshold"/> is not positive.</exception>
    public IntegrityAnchoringBuilder WithDelayThreshold(TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        Options.DelayThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the interval between anchoring runs. Defaults to 5 minutes.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="interval"/> is not positive.</exception>
    public IntegrityAnchoringBuilder WithInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Options.Interval = interval;
        return this;
    }

    /// <summary>
    /// Sets the number of leaves after which a run stops reading candidates. Defaults to 100,000.
    /// </summary>
    /// <param name="maximum">The maximum.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maximum"/> is not positive.</exception>
    public IntegrityAnchoringBuilder WithMaxLeavesPerAnchor(Int32 maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        Options.MaxLeavesPerAnchor = maximum;
        return this;
    }

    /// <summary>
    /// Sets the number of valid timestamp tokens an anchor needs to complete. Defaults to 1; it must not exceed the
    /// number of authorities, which is checked once the configuration is complete.
    /// </summary>
    /// <param name="minimum">The minimum.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="minimum"/> is not positive.</exception>
    public IntegrityAnchoringBuilder WithMinimumTimestampTokens(Int32 minimum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimum);
        Options.MinimumTimestampTokens = minimum;
        return this;
    }

    /// <summary>
    /// Sets the age of the oldest timestamp token with an unconfirmed revocation status above which the revocation
    /// confirmation delay is reported. Defaults to 72 hours; it must exceed the revocation grace period, which is
    /// checked once the configuration is complete.
    /// </summary>
    /// <param name="threshold">The threshold.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="threshold"/> is not positive.</exception>
    public IntegrityAnchoringBuilder WithRevocationConfirmationDelayThreshold(TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        Options.RevocationConfirmationDelayThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the time after a token's time that the CRLs confirming its revocation status must be issued at or after.
    /// Defaults to 24 hours.
    /// </summary>
    /// <param name="gracePeriod">The grace period.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="gracePeriod"/> is not positive.</exception>
    public IntegrityAnchoringBuilder WithRevocationGracePeriod(TimeSpan gracePeriod)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gracePeriod, TimeSpan.Zero);
        Options.RevocationGracePeriod = gracePeriod;
        return this;
    }

    /// <summary>
    /// Adds an RFC 3161 timestamp authority reached over HTTP.
    /// </summary>
    /// <param name="name">The name that identifies the authority in stored anchors.</param>
    /// <param name="uri">The URI that accepts timestamp queries.</param>
    /// <param name="configure">Configures the HTTP client factory and the trust settings.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is empty or already used.</exception>
    public IntegrityAnchoringBuilder WithRfc3161TimestampAuthority(String name, Uri uri, Action<Rfc3161TimestampAuthorityOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(uri);
        if (!_authorityNames.Add(name))
        {
            throw new ArgumentException($"The timestamp authority name '{name}' is used more than once.", nameof(name));
        }

        var options = new Rfc3161TimestampAuthorityOptions(name, uri);
        configure?.Invoke(options);
        _rfc3161Authorities.Add(options);
        _authorities.Add(serviceProvider => new Rfc3161TimestampAuthority(options, serviceProvider));
        return this;
    }

    /// <summary>
    /// Adds a custom timestamp authority, for example for an API other than RFC 3161 over HTTP that still returns an
    /// RFC 3161 token.
    /// </summary>
    /// <remarks>
    /// Authority names must be unique. For custom authorities this is checked when the anchoring run is created,
    /// because they only exist once the service provider is built. Anchoring checks every token a custom authority
    /// returns before storing it, see <see cref="ITimestampAuthority.TimestampAsync"/>; trust its roots for
    /// verification with <c>WithIntegrityVerification</c>.
    /// </remarks>
    /// <param name="factory">Creates the authority.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder WithTimestampAuthority(Func<IServiceProvider, ITimestampAuthority> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _authorities.Add(factory);
        return this;
    }

    /// <summary>
    /// Creates the configured authorities.
    /// </summary>
    internal IReadOnlyList<ITimestampAuthority> CreateAuthorities(IServiceProvider serviceProvider)
        => _authorities.Select(factory => factory(serviceProvider)).ToList();

    /// <summary>
    /// Throws when the configuration cannot anchor.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no authority is configured, the minimum number of tokens exceeds the number of authorities, or the
    /// revocation confirmation delay threshold does not exceed the revocation grace period.
    /// </exception>
    internal void Validate()
    {
        if (_authorities.Count == 0)
        {
            throw new InvalidOperationException(
                "Integrity anchoring needs at least one timestamp authority, and there is no default authority. " +
                $"Add one with {nameof(WithRfc3161TimestampAuthority)} or {nameof(WithTimestampAuthority)}.");
        }

        if (Options.MinimumTimestampTokens > _authorities.Count)
        {
            throw new InvalidOperationException(
                $"The minimum number of timestamp tokens per anchor must not exceed the number of authorities ({_authorities.Count}).");
        }

        if (Options.RevocationConfirmationDelayThreshold <= Options.RevocationGracePeriod)
        {
            throw new InvalidOperationException("The revocation grace period must be less than the revocation confirmation delay threshold.");
        }
    }
}
