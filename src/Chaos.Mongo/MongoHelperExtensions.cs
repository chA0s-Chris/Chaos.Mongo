// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

/// <summary>
/// Extensions for <see cref="IMongoHelper"/>.
/// </summary>
public static class MongoHelperExtensions
{
    /// <summary>
    /// Acquire a distributed lock in MongoDB with automatic retry on contention.
    /// </summary>
    /// <remarks>
    /// This method will retry indefinitely until the lock is acquired or the <paramref name="cancellationToken"/> is
    /// cancelled.
    /// The lock is automatically released when the returned <see cref="IMongoLock"/> is disposed.
    /// If the lock is not explicitly released, it will automatically expire after <paramref name="leaseTime"/>.
    /// </remarks>
    /// <param name="helper">The MongoDB helper instance.</param>
    /// <param name="lockName">The name of the lock to acquire.</param>
    /// <param name="leaseTime">Optional lease duration. Defaults to <see cref="MongoDefaults.LockLeaseTime"/>.</param>
    /// <param name="retryDelay">Optional delay between retry attempts. Defaults to <see cref="MongoDefaults.RetryDelay"/>.</param>
    /// <param name="cancellationToken">Optional cancellation token to stop lock acquisition attempts.</param>
    /// <returns>A lock instance that must be disposed to release the lock.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="lockName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leaseTime"/> is shorter than one millisecond.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    public static async Task<IMongoLock> AcquireLockAsync(this IMongoHelper helper,
                                                          String lockName,
                                                          TimeSpan? leaseTime = null,
                                                          TimeSpan? retryDelay = null,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        retryDelay ??= MongoDefaults.RetryDelay;

        // Retry until lock is acquired or cancellation requested
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lockInstance = await helper.TryAcquireLockAsync(lockName, leaseTime, cancellationToken);
            if (lockInstance is not null)
                return lockInstance;

            // Lock not available, wait and retry
            await Task.Delay(retryDelay.Value, cancellationToken);
        }
    }

    /// <summary>
    /// Execute code within a MongoDB transaction with automatic retry on transient errors.
    /// </summary>
    /// <remarks>
    /// If the code completes without error the transaction will be committed.
    /// Use <see cref="IClientSessionHandle"/> to abort the transaction when necessary.
    /// When transient errors are encountered the transaction will be retried. This only works if
    /// <paramref name="callback"/> does not suppress <see cref="MongoException"/>s.
    /// </remarks>
    /// <param name="helper">The MongoDB helper instance.</param>
    /// <param name="callback">Code to execute within the transaction.</param>
    /// <param name="transactionOptions">Optional transaction options.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <typeparam name="TResult">Type of the return value.</typeparam>
    /// <returns>Value returned by <paramref name="callback"/>.</returns>
    public static async Task<TResult> ExecuteInTransaction<TResult>(this IMongoHelper helper,
                                                                    Func<IMongoHelper, IClientSessionHandle, CancellationToken, Task<TResult>> callback,
                                                                    TransactionOptions? transactionOptions = null,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(callback);

        using var session = await helper.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync(
            async (sessionHandle, token) => await callback.Invoke(helper, sessionHandle, token),
            transactionOptions,
            cancellationToken);
    }

    /// <summary>
    /// Execute code within a MongoDB transaction with automatic retry on transient errors.
    /// </summary>
    /// <remarks>
    /// If the code completes without error the transaction will be committed.
    /// Use <see cref="IClientSessionHandle"/> to abort the transaction when necessary.
    /// When transient errors are encountered the transaction will be retried. This only works if
    /// <paramref name="callback"/> does not suppress <see cref="MongoException"/>s.
    /// </remarks>
    /// <param name="helper">The MongoDB helper instance.</param>
    /// <param name="callback">Code to execute within the transaction.</param>
    /// <param name="transactionOptions">Optional transaction options.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task ExecuteInTransaction(this IMongoHelper helper,
                                                  Func<IMongoHelper, IClientSessionHandle, CancellationToken, Task> callback,
                                                  TransactionOptions? transactionOptions = null,
                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(callback);

        using var session = await helper.Client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync<Object?>(
            async (sessionHandle, token) =>
            {
                await callback.Invoke(helper, sessionHandle, token);
                return null;
            },
            transactionOptions,
            cancellationToken);
    }

    /// <summary>
    /// Attempts to start a MongoDB client session without throwing exceptions.
    /// </summary>
    /// <param name="helper">The MongoDB helper instance.</param>
    /// <param name="options">Optional session options.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A client session if successful, or <c>null</c> if a <see cref="MongoException"/> occurs.</returns>
    /// <remarks>
    ///     <para>
    ///     This method is useful when you want to use sessions if available but continue without them if not supported.
    ///     For example, standalone MongoDB servers do not support sessions.
    ///     </para>
    ///     <para>
    ///     The caller is responsible for disposing the returned session.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="helper"/> is null.</exception>
    public static async Task<IClientSessionHandle?> TryStartSessionAsync(this IMongoHelper helper,
                                                                         ClientSessionOptions? options = null,
                                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helper);

        try
        {
            return await helper.Client.StartSessionAsync(options, cancellationToken);
        }
        catch (MongoException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to start a MongoDB transaction without throwing exceptions.
    /// </summary>
    /// <param name="helper">The MongoDB helper instance.</param>
    /// <param name="sessionOptions">Optional session options.</param>
    /// <param name="transactionOptions">Optional transaction options.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// A client session with an active transaction if successful, or <c>null</c> if transactions are not supported or
    /// an error occurs.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///     This method is useful when you want to use transactions if available but continue without them if not supported.
    ///     Transactions may not be available in several scenarios:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>Standalone MongoDB servers (transactions require replica sets or sharded clusters)</description>
    ///         </item>
    ///         <item>
    ///             <description>MongoDB versions older than 4.0</description>
    ///         </item>
    ///         <item>
    ///             <description>Storage engines that don't support transactions</description>
    ///         </item>
    ///     </list>
    ///     </para>
    ///     <para>
    ///     If the session is created but the transaction fails to start, the session is automatically disposed.
    ///     The caller is responsible for disposing the returned session if not null.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="helper"/> is null.</exception>
    public static async Task<IClientSessionHandle?> TryStartTransactionAsync(this IMongoHelper helper,
                                                                             ClientSessionOptions? sessionOptions = null,
                                                                             TransactionOptions? transactionOptions = null,
                                                                             CancellationToken cancellationToken = default)
    {
        var session = await helper.TryStartSessionAsync(sessionOptions, cancellationToken);
        if (session is null)
        {
            return null;
        }

        try
        {
            session.StartTransaction(transactionOptions);
            return session;
        }
        catch
        {
            // there could be multiple reasons for this failure, dispose the session
            session.Dispose();
            return null;
        }
    }
}
