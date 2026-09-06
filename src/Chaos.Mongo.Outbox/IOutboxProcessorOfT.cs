// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

/// <summary>
/// Controls the independent processor for the outbox identified by <typeparamref name="TOutbox"/>.
/// </summary>
/// <typeparam name="TOutbox">The destination marker type, which is never instantiated.</typeparam>
public interface IOutboxProcessor<TOutbox> : IOutboxProcessor;
