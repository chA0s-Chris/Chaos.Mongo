// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

/// <summary>
/// Writes messages to the outbox identified by <typeparamref name="TOutbox"/> in the caller's transaction.
/// </summary>
/// <typeparam name="TOutbox">The destination marker type, which is never instantiated.</typeparam>
public interface IOutbox<TOutbox> : IOutbox;
