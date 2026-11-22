// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Bindings;

/// <summary>
/// Hand-crafted <see cref="IClientSessionHandle"/> for tests that only pass a session on and never use it.
/// </summary>
internal sealed class FakeClientSessionHandle : IClientSessionHandle
{
    public IMongoClient Client => throw new NotSupportedException();
    public BsonDocument ClusterTime => throw new NotSupportedException();
    public Boolean IsImplicit => false;
    public Boolean IsInTransaction => false;
    public BsonTimestamp OperationTime => throw new NotSupportedException();
    public ClientSessionOptions Options => throw new NotSupportedException();
    public IServerSession ServerSession => throw new NotSupportedException();
    public ICoreSessionHandle WrappedCoreSession => throw new NotSupportedException();

    public void AbortTransaction(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task AbortTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void AdvanceClusterTime(BsonDocument newClusterTime) => throw new NotSupportedException();

    public void AdvanceOperationTime(BsonTimestamp newOperationTime) => throw new NotSupportedException();

    public void CommitTransaction(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void StartTransaction(TransactionOptions? transactionOptions = null) => throw new NotSupportedException();

    public TResult WithTransaction<TResult>(Func<IClientSessionHandle, CancellationToken, TResult> callback,
                                            TransactionOptions? transactionOptions = null,
                                            CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<TResult> WithTransactionAsync<TResult>(Func<IClientSessionHandle, CancellationToken, Task<TResult>> callbackAsync,
                                                       TransactionOptions? transactionOptions = null,
                                                       CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IClientSessionHandle Fork() => throw new NotSupportedException();

    public void Dispose() { }
}
