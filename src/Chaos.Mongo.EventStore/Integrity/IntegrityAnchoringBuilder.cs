// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Configures the module-level anchoring pipeline. There is no default timestamp authority.
/// </summary>
internal sealed class IntegrityAnchoringBuilder
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
    /// Gets the anchoring options this builder fills.
    /// </summary>
    public IntegrityAnchoringOptions Options { get; }

    /// <summary>
    /// Gets the completion hook, if one is configured.
    /// </summary>
    internal Func<IServiceProvider, CompletedAnchor, CancellationToken, Task>? CompletionHook { get; private set; }

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
    /// Sets the age of the oldest unanchored candidate above which the anchor delay is reported. Defaults to 1 hour.
    /// </summary>
    /// <param name="threshold">The threshold.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder WithDelayThreshold(TimeSpan threshold)
    {
        Options.DelayThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the interval between anchoring runs. Defaults to 5 minutes.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder WithInterval(TimeSpan interval)
    {
        Options.Interval = interval;
        return this;
    }

    /// <summary>
    /// Sets the number of leaves after which a run stops reading candidates. Defaults to 100,000.
    /// </summary>
    /// <param name="maximum">The maximum.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder WithMaxLeavesPerAnchor(Int32 maximum)
    {
        Options.MaxLeavesPerAnchor = maximum;
        return this;
    }

    /// <summary>
    /// Sets the number of valid timestamp tokens an anchor needs to complete. Defaults to 1; it must not exceed the
    /// number of authorities.
    /// </summary>
    /// <param name="minimum">The minimum.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder WithMinimumTimestampTokens(Int32 minimum)
    {
        Options.MinimumTimestampTokens = minimum;
        return this;
    }

    /// <summary>
    /// Sets the age of the oldest timestamp token with an unconfirmed revocation status above which the revocation
    /// confirmation delay is reported. Defaults to 72 hours; it must exceed the revocation grace period.
    /// </summary>
    /// <param name="threshold">The threshold.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder WithRevocationConfirmationDelayThreshold(TimeSpan threshold)
    {
        Options.RevocationConfirmationDelayThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the time after a token's time that the CRLs confirming its revocation status must be issued at or after.
    /// Defaults to 24 hours.
    /// </summary>
    /// <param name="gracePeriod">The grace period.</param>
    /// <returns>The builder for method chaining.</returns>
    public IntegrityAnchoringBuilder WithRevocationGracePeriod(TimeSpan gracePeriod)
    {
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

        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = name,
            Uri = uri
        };
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
    /// because they only exist once the service provider is built.
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
    /// <exception cref="InvalidOperationException">Thrown when no authority is configured or a setting is out of range.</exception>
    internal void Validate()
    {
        if (_authorities.Count == 0)
        {
            throw new InvalidOperationException(
                "Integrity anchoring needs at least one timestamp authority, and there is no default authority. " +
                $"Add one with {nameof(WithRfc3161TimestampAuthority)} or {nameof(WithTimestampAuthority)}.");
        }

        if (Options.MinimumTimestampTokens < 1 || Options.MinimumTimestampTokens > _authorities.Count)
        {
            throw new InvalidOperationException(
                $"The minimum number of timestamp tokens per anchor must be between 1 and the number of authorities ({_authorities.Count}).");
        }

        if (Options.Interval <= TimeSpan.Zero || Options.DelayThreshold <= TimeSpan.Zero || Options.MaxLeavesPerAnchor < 1)
        {
            throw new InvalidOperationException("The anchoring interval, the delay threshold and the maximum number of leaves must be positive.");
        }

        if (Options.RevocationGracePeriod <= TimeSpan.Zero || Options.RevocationConfirmationDelayThreshold <= Options.RevocationGracePeriod)
        {
            throw new InvalidOperationException("The revocation grace period must be positive and less than the revocation confirmation delay threshold.");
        }
    }
}
