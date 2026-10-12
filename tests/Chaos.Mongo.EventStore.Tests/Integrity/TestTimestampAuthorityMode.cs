// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

/// <summary>
/// How a <see cref="TestTimestampAuthority"/> answers.
/// </summary>
public enum TestTimestampAuthorityMode
{
    Valid,
    WrongImprint,
    WrongNonce,
    BrokenSignature,
    WrongEssCertId,
    Backdated,
    FutureDated,
    Rejection,
    TrailingData,
    HttpFailure,
    Unreachable,
    Timeout,
    CrlPrimaryUnavailable,
    CrlUnavailable,
    CrlEmpty
}
