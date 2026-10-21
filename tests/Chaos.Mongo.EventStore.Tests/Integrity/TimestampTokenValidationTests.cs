// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;

public class TimestampTokenValidationTests
{
    [Test]
    public void ValidateSigner_CriticalTimestampingPurpose_Passes()
    {
        using var authority = new TestTimestampAuthority();
        var signer = authority.SigningCertificate;

        var act = () => TimestampTokenValidation.ValidateSigner(signer);

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateSigner_NonCriticalPurpose_ThrowsTimestampTokenValidationException()
    {
        using var authority = new TestTimestampAuthority(nonCriticalPurpose: true);
        var signer = authority.SigningCertificate;

        var act = () => TimestampTokenValidation.ValidateSigner(signer);

        act.Should().Throw<TimestampTokenValidationException>().WithMessage("*must be critical*");
    }
}
