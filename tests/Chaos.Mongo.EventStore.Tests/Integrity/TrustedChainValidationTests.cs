// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Tests <see cref="TimestampTokenValidation.MatchesTrustedChain"/> and
/// <see cref="TimestampTokenValidation.BuildIsolatedChain"/>:
/// Check A (the stored chain's signer and intermediates remain genuinely, uniquely trusted today) and Check B (the
/// stored chain's own recorded root is independently genuine).
/// </summary>
public class TrustedChainValidationTests
{
    /// <summary>
    /// The object identifier of the X.509 <c>NameConstraints</c> extension (RFC 5280, section 4.2.1.10).
    /// </summary>
    private const String NameConstraintsOid = "2.5.29.30";

    [Test]
    public void BuildIsolatedChain_SameRootSiblings_DoesNotLeakUnofferedSiblingIntoResult()
    {
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Isolation Root");
        var (siblingA, siblingAKey) = CreateIntermediate("CN=Chaos.Mongo Isolation Sibling A", root, rootKey);
        var siblingB = CreateCrossSigned(siblingA, root, rootKey);
        var signer = CreateSigner("CN=Chaos.Mongo Isolation Signer", siblingA, siblingAKey);

        var built = TimestampTokenValidation.BuildIsolatedChain(signer, DateTimeOffset.UtcNow, [siblingA], root, X509RevocationMode.NoCheck, TimeSpan.Zero);

        built.Should().NotBeNull();
        built.Should().HaveCount(3);
        built[1].Should().Equal(siblingA.RawData, "the isolated call must use only the offered sibling, never the unoffered one sharing its key and root");
        built.Should().NotContain(certificate => certificate.SequenceEqual(siblingB.RawData));
    }

    [Test]
    public void CreateToken_ExtraCertificates_AreEmbeddedBesideTheSigner()
    {
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Embedded Extra Root");
        var (intermediate, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Embedded Extra Intermediate", root, rootKey);
        var signer = CreateSigner("CN=Chaos.Mongo Embedded Extra Signer", intermediate, intermediateKey);

        var token = CreateToken(signer, SHA256.HashData(Guid.NewGuid().ToByteArray()), extraCertificates: [intermediate]);

        var embedded = token.AsSignedCms().Certificates;
        embedded.Should().HaveCount(2, "the signer is always embedded, beside every certificate CreateToken was asked to add");
        embedded.Select(certificate => certificate.RawData)
                .Should()
                .Contain(certificate => certificate.SequenceEqual(signer.RawData))
                .And.Contain(certificate => certificate.SequenceEqual(intermediate.RawData));
    }

    [Test]
    public void MatchesTrustedChain_CertificateMultisetReordered_IsRejected()
    {
        // intermediateA and intermediateB genuinely share one subject name, at two different, adjacent positions of
        // the real path, each carrying a Subject/Authority Key Identifier exactly as a genuine, reissued CA
        // certificate would (RFC 5280, 4.2.1.1/4.2.1.2): the engine disambiguates same-named candidates by key
        // identifier, not subject name alone, so BuildChain itself resolves the genuinely correct order even though
        // the two candidates share a name, and MatchesTrustedChain still rejects the same multiset recorded swapped.
        const String sharedName = "CN=Chaos.Mongo Reorder Twin";
        var (root, rootKey) = CreateRootWithKeyIdentifier("CN=Chaos.Mongo Reorder Root");
        var (intermediateA, intermediateAKey) = CreateIntermediateWithKeyIdentifier(sharedName, root, rootKey);
        var (intermediateB, intermediateBKey) = CreateIntermediateWithKeyIdentifier(sharedName, intermediateA, intermediateAKey);
        var signer = CreateSignerWithKeyIdentifier("CN=Chaos.Mongo Reorder Signer", intermediateB, intermediateBKey);
        var token = CreateToken(signer);
        var settings = new ChainSettings(new X509Certificate2Collection(root),
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection
                                         {
                                             intermediateA,
                                             intermediateB
                                         });

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        // The key identifier lets the engine resolve the genuinely correct order despite the shared subject name.
        built.Certificates.Select(Convert.ToHexString)
             .Should()
             .Equal(Convert.ToHexString(signer.RawData), Convert.ToHexString(intermediateB.RawData),
                    Convert.ToHexString(intermediateA.RawData), Convert.ToHexString(root.RawData));

        var genuineStored = new X509Certificate2Collection
        {
            signer,
            intermediateB,
            intermediateA,
            root
        };
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, genuineStored, settings)
                                .Should()
                                .BeTrue("the stored chain records the genuinely correct order the engine itself built");

        var reorderedStored = new X509Certificate2Collection
        {
            signer,
            intermediateA,
            intermediateB,
            root
        };
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, reorderedStored, settings)
                                .Should()
                                .BeFalse("intermediateA and intermediateB are swapped, even though both certificates and the root are the same multiset");
    }

    [Test]
    public void MatchesTrustedChain_ChainAlreadyExpiredAtTokenTime_BuildChainRejectsIt()
    {
        // RFC 3161 requires the signing certificate's own validity period to contain the token's time (enforced by
        // Rfc3161TimestampToken.TryDecode itself), so only the root's validity period can genuinely have already
        // ended at the token's own time while the token itself still decodes.
        var now = DateTimeOffset.UtcNow;
        var tokenTime = now.AddDays(-20); // after the root's own validity window
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Expired Window Root", notBefore: now.AddDays(-90), notAfter: now.AddDays(-25));
        var signer = CreateSigner("CN=Chaos.Mongo Expired Window Signer", root, rootKey, now.AddDays(-60), now.AddDays(-10));
        var token = CreateToken(signer, tokenTime);
        var settings = new ChainSettings(new X509Certificate2Collection(root), X509RevocationMode.NoCheck, TimeSpan.Zero, new X509Certificate2Collection());

        Action act = () => TimestampTokenValidation.BuildChain(token, signer, settings);

        act.Should().Throw<TimestampTokenValidationException>("the chain was already expired at the token's own time, not merely by today's clock");
    }

    [Test]
    public void MatchesTrustedChain_ChainGenuinelyValidOnlyAtTokenTime_IsAcceptedAtThatTime()
    {
        var now = DateTimeOffset.UtcNow;
        var validFrom = now.AddDays(-60);
        var validUntil = now.AddDays(-30);
        var tokenTime = now.AddDays(-45);
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Time Window Root", notBefore: now.AddDays(-90), notAfter: now.AddDays(10));
        var signer = CreateSigner("CN=Chaos.Mongo Time Window Signer", root, rootKey, validFrom, validUntil);
        var token = CreateToken(signer, tokenTime);
        var settings = new ChainSettings(new X509Certificate2Collection(root), X509RevocationMode.NoCheck, TimeSpan.Zero, new X509Certificate2Collection());
        var stored = new X509Certificate2Collection
        {
            signer,
            root
        };

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, stored, settings)
                                .Should()
                                .BeTrue("the chain is genuinely valid at the token's own time, even though it has since expired");
    }

    [Test]
    public void MatchesTrustedChain_ExtendedKeyUsageRestrictionViolatedOnlyAcrossCompletePath_BuildChainRejectsIt()
    {
        // The intermediate's own extended key usage excludes timestamping (clientAuth only); each individual link is
        // genuine, but the complete path's application-policy requirement (id-kp-timeStamping) fails because of it.
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo EKU Root");
        var (intermediate, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo EKU Intermediate",
                                                                 root,
                                                                 rootKey,
                                                                 extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"]);
        var signer = CreateSigner("CN=Chaos.Mongo EKU Signer", intermediate, intermediateKey);
        var token = CreateToken(signer);
        var settings = new ChainSettings(new X509Certificate2Collection(root),
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection(intermediate));

        Action act = () => TimestampTokenValidation.BuildChain(token, signer, settings);

        act.Should().Throw<TimestampTokenValidationException>("the intermediate's extended key usage excludes timestamping, failing the complete path's application policy");
    }

    [Test]
    public async Task MatchesTrustedChain_LongerBridgedPathToAnotherTrustedRoot_IsAccepted()
    {
        using var authority = new TestTimestampAuthority();
        var (token, signer) = await StampAsync(authority);
        var (rootB, rootBKey) = CreateRoot("CN=Chaos.Mongo Bridge Root B");
        var bridge = CreateCrossSigned(authority.Root, rootB, rootBKey);
        var settings = new ChainSettings(new X509Certificate2Collection(rootB),
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection(bridge));
        var stored = new X509Certificate2Collection
        {
            signer,
            authority.Root
        };

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        built.Certificates.Should().HaveCount(3, "rootA alone is not trusted, so the engine must bridge through rootB");
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, stored, settings)
                                .Should()
                                .BeTrue("the signer is pinned, and the stored chain's own root independently, genuinely terminates it");
    }

    [Test]
    public void MatchesTrustedChain_NameConstraintViolatedOnlyAcrossCompletePath_BuildChainRejectsIt()
    {
        // The root excludes the signer's own subject name from its certification path; each individual link below
        // is genuine, but the complete path's inherited name constraint fails because of the signer's name. A
        // placeholder certificate of the signer's intended subject name supplies the excluded subtree's encoding
        // (EncodeNameConstraints only reads its SubjectName), since the signer cannot be created before the root
        // that must name it in a constraint.
        const String signerSubjectName = "CN=Chaos.Mongo Name Constraint Signer";
        using var placeholder = CreateRoot(signerSubjectName).Certificate;
        var nameConstraints = EncodeNameConstraints(excluded: placeholder);
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Name Constraint Root", nameConstraints);
        var (intermediate, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Name Constraint Intermediate", root, rootKey);
        var signer = CreateSigner(signerSubjectName, intermediate, intermediateKey);
        var token = CreateToken(signer);
        var settings = new ChainSettings(new X509Certificate2Collection(root),
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection(intermediate));

        Action act = () => TimestampTokenValidation.BuildChain(token, signer, settings);

        act.Should().Throw<TimestampTokenValidationException>(
            "the root's name constraint excludes the signer's subject across the complete path even though every recorded link is genuine");
    }

    [Test]
    public void MatchesTrustedChain_NonFinalSelfIssuedIntermediateExemptFromRootsOwnNameConstraint_IsAccepted()
    {
        // The reissued certificate shares the root's own subject name and is issued by it, so it is self-issued by
        // definition (RFC 5280, 4.2.1.10): issuer == subject. The root's own name constraint excludes that same
        // subject name, which would reject the reissued certificate like any other non-self-issued certificate —
        // except the self-issued, non-final exemption applies, since a signer still follows it in the complete path.
        const String sharedName = "CN=Chaos.Mongo Reissued CA";
        using var placeholder = CreateRoot(sharedName).Certificate;
        var nameConstraints = EncodeNameConstraints(excluded: placeholder);
        var (root, rootKey) = CreateRootWithKeyIdentifier(sharedName, nameConstraints);
        var (reissued, reissuedKey) = CreateIntermediateWithKeyIdentifier(sharedName, root, rootKey);
        var signer = CreateSignerWithKeyIdentifier("CN=Chaos.Mongo Reissued CA Signer", reissued, reissuedKey);
        var token = CreateToken(signer);
        var settings = new ChainSettings(new X509Certificate2Collection(root),
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection(reissued));

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        built.Certificates.Select(Convert.ToHexString)
             .Should()
             .Equal(Convert.ToHexString(signer.RawData), Convert.ToHexString(reissued.RawData), Convert.ToHexString(root.RawData));
        var stored = new X509Certificate2Collection
        {
            signer,
            reissued,
            root
        };
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, stored, settings)
                                .Should()
                                .BeTrue("the reissued certificate is self-issued and not the path's own final certificate, so the root's name constraint exempts it");
    }

    [Test]
    public void MatchesTrustedChain_PathLengthConstraintViolatedOnlyAcrossCompletePath_BuildChainRejectsIt()
    {
        // The root permits no subordinate CA to follow it (pathLenConstraint 0); each individual link below is
        // genuine, but the complete path has one CA certificate (the intermediate) following the root.
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Path Length Root", pathLengthConstraint: 0);
        var (intermediate, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Path Length Intermediate", root, rootKey, 0);
        var signer = CreateSigner("CN=Chaos.Mongo Path Length Signer", intermediate, intermediateKey);
        var token = CreateToken(signer);
        var settings = new ChainSettings(new X509Certificate2Collection(root),
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection(intermediate));

        Action act = () => TimestampTokenValidation.BuildChain(token, signer, settings);

        act.Should().Throw<TimestampTokenValidationException>("the root's path-length constraint is violated across the complete path even though every recorded link is genuine");
    }

    [Test]
    public void MatchesTrustedChain_SharedKeySiblingReachableThroughAnotherRoot_IsRejectedAsNonUnique()
    {
        var (rootA, rootAKey) = CreateRoot("CN=Chaos.Mongo Sibling Root A");
        var (rootB, rootBKey) = CreateRoot("CN=Chaos.Mongo Sibling Root B");
        var (intermediateA, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Sibling Intermediate", rootA, rootAKey);
        var intermediateB = CreateCrossSigned(intermediateA, rootB, rootBKey);
        var signer = CreateSigner("CN=Chaos.Mongo Sibling Signer", intermediateA, intermediateKey);
        var token = CreateToken(signer);
        var stored = new X509Certificate2Collection
        {
            signer,
            intermediateA,
            rootA
        };
        var settings = new ChainSettings(new X509Certificate2Collection
                                         {
                                             rootA,
                                             rootB
                                         },
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection
                                         {
                                             intermediateA,
                                             intermediateB,
                                             rootB
                                         });

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        // The shortest genuine path must match the stored chain.
        built.Certificates.Select(Convert.ToHexString)
             .Should()
             .Equal(Convert.ToHexString(signer.RawData), Convert.ToHexString(intermediateA.RawData), Convert.ToHexString(rootA.RawData));
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, stored, settings)
                                .Should()
                                .BeFalse("intermediateB, sharing the recorded position's key and subject, reaches its own independently trusted root");
    }

    [Test]
    public void MatchesTrustedChain_SharedKeySiblingWhoseOnlyAlternatePathIsExtendedKeyUsageIneligible_IsAccepted()
    {
        var (rootA, rootAKey) = CreateRoot("CN=Chaos.Mongo Ineligible Sibling Root A");
        var (rootB, rootBKey) = CreateRoot("CN=Chaos.Mongo Ineligible Sibling Root B");
        var (intermediateA, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Ineligible Sibling Intermediate", rootA, rootAKey);
        // intermediateB shares intermediateA's subject and key, so it is graph-connected to rootB by direct
        // cryptographic issuer adjacency alone, but its own extended key usage excludes timestamping, so no complete
        // chain through it can satisfy the application policy every built chain requires.
        var intermediateB = CreateCrossSigned(intermediateA, rootB, rootBKey, extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"]);
        var signer = CreateSigner("CN=Chaos.Mongo Ineligible Sibling Signer", intermediateA, intermediateKey);
        var token = CreateToken(signer);
        var stored = new X509Certificate2Collection
        {
            signer,
            intermediateA,
            rootA
        };
        var settings = new ChainSettings(new X509Certificate2Collection
                                         {
                                             rootA,
                                             rootB
                                         },
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection
                                         {
                                             intermediateA,
                                             intermediateB,
                                             rootB
                                         });

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        built.Certificates.Select(Convert.ToHexString)
             .Should()
             .Equal(Convert.ToHexString(signer.RawData), Convert.ToHexString(intermediateA.RawData), Convert.ToHexString(rootA.RawData));
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, stored, settings)
                                .Should()
                                .BeTrue("the only remaining alternate sibling is itself constraint-ineligible, so the recorded position is genuinely unique");
    }

    [Test]
    public async Task MatchesTrustedChain_SubstitutedRootNeverIssuedTheIntermediate_IsRejected()
    {
        using var authority = new TestTimestampAuthority();
        var (token, signer) = await StampAsync(authority);
        var (decoyRoot, _) = CreateRoot(authority.Root.Subject);
        var settings = new ChainSettings(new X509Certificate2Collection(authority.Root), X509RevocationMode.NoCheck, TimeSpan.Zero, new X509Certificate2Collection());
        var substitutedStored = new X509Certificate2Collection
        {
            signer,
            decoyRoot
        };

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, substitutedStored, settings)
                                .Should()
                                .BeFalse("the decoy root shares the genuine root's subject but never issued the signer");
    }

    [Test]
    public void MatchesTrustedChain_ThreeSharedKeySiblingsEachConstraintIneligibleForADifferentReason_IsAccepted()
    {
        // Generalizes the two-sibling case to three: one alternate is extended-key-usage-ineligible, the other's
        // root excludes the signer's own name, proving the uniqueness search checks every alternate rather than
        // stopping once it has ruled out the first one it happens to try.
        //
        // Unlike the two-sibling case, three same-subject, same-key candidates make .NET's own chain.Build()
        // resolution of "the" chain (used only to decide which single candidate BuildChain returns) non-deterministic:
        // regenerating this construction with fresh keys can make the engine pick an ineligible sibling first and
        // fail the whole build, even though a valid path through intermediateA exists. Each sibling therefore gets
        // its own explicit Subject Key Identifier (not derived from their shared key, so it still differs between
        // them), and the signer's Authority Key Identifier names intermediateA's specifically -- the same
        // identifier-based disambiguation CreateSignerWithKeyIdentifier/MatchesTrustedChain_CertificateMultisetReordered_IsRejected
        // already rely on to resolve a same-named candidate deterministically. This affects only chain.Build()'s own
        // resolution: DirectSignatureVerification.IsDirectIssuer, which the uniqueness search's alternate-path
        // discovery depends on, matches by issuer name and signature alone, never by key identifier, so the search
        // still discovers and rejects both siblings on their own merits.
        var (rootA, rootAKey) = CreateRoot("CN=Chaos.Mongo Three Sibling Root A");
        var (rootB, rootBKey) = CreateRoot("CN=Chaos.Mongo Three Sibling Root B");
        const String signerSubjectName = "CN=Chaos.Mongo Three Sibling Signer";
        using var placeholder = CreateRoot(signerSubjectName).Certificate;
        var nameConstraints = EncodeNameConstraints(excluded: placeholder);
        var (rootC, rootCKey) = CreateRoot("CN=Chaos.Mongo Three Sibling Root C", nameConstraints);
        var intermediateASubjectKeyIdentifier = RandomNumberGenerator.GetBytes(20);
        var (intermediateA, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Three Sibling Intermediate", rootA, rootAKey,
                                                                  subjectKeyIdentifier: intermediateASubjectKeyIdentifier);
        // intermediateB shares intermediateA's subject and key but is extended-key-usage-ineligible for timestamping.
        var intermediateB = CreateCrossSigned(intermediateA, rootB, rootBKey, extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"],
                                              subjectKeyIdentifier: RandomNumberGenerator.GetBytes(20));
        // intermediateC shares the same subject and key, but rootC's own name constraint excludes the signer's name
        // across the complete path through it.
        var intermediateC = CreateCrossSigned(intermediateA, rootC, rootCKey, subjectKeyIdentifier: RandomNumberGenerator.GetBytes(20));
        var signer = CreateSigner(signerSubjectName, intermediateA, intermediateKey, issuerSubjectKeyIdentifier: intermediateASubjectKeyIdentifier);
        var token = CreateToken(signer);
        var stored = new X509Certificate2Collection
        {
            signer,
            intermediateA,
            rootA
        };
        var settings = new ChainSettings(new X509Certificate2Collection
                                         {
                                             rootA,
                                             rootB,
                                             rootC
                                         },
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection
                                         {
                                             intermediateA,
                                             intermediateB,
                                             intermediateC,
                                             rootB,
                                             rootC
                                         });

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        built.Certificates.Select(Convert.ToHexString)
             .Should()
             .Equal(Convert.ToHexString(signer.RawData), Convert.ToHexString(intermediateA.RawData), Convert.ToHexString(rootA.RawData));
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, stored, settings)
                                .Should()
                                .BeTrue("both alternate siblings are themselves constraint-ineligible for different reasons, so the recorded position is genuinely unique");
    }

    [Test]
    public void MatchesTrustedChain_TwoRecordedIntermediatesOneWithSiblingReachableThroughAnotherRoot_IsRejected()
    {
        // The sibling sits at the outer position (reachable through rootB directly, not through any further
        // bridge), while inner is the one certificate every path -- recorded or alternate -- must pass through:
        // signer has no other direct issuer, so excluding inner alone leaves no alternate at all (it stays genuinely
        // silent), while excluding outer still finds the alternate through outerSibling. A sibling placed at the
        // inner position instead would let its own alternate path bypass outer entirely, so excluding either
        // position would find the same alternate and never actually isolate which one is unique.
        var (rootA, rootAKey) = CreateRoot("CN=Chaos.Mongo Two-Intermediate Root A");
        var (rootB, rootBKey) = CreateRoot("CN=Chaos.Mongo Two-Intermediate Root B");
        var (outer, outerKey) = CreateIntermediate("CN=Chaos.Mongo Two-Intermediate Outer", rootA, rootAKey, 1);
        var (inner, innerKey) = CreateIntermediate("CN=Chaos.Mongo Two-Intermediate Inner", outer, outerKey);
        var outerSibling = CreateCrossSigned(outer, rootB, rootBKey, 1);
        var signer = CreateSigner("CN=Chaos.Mongo Two-Intermediate Signer", inner, innerKey);
        var token = CreateToken(signer);
        var settingsWithoutSibling = new ChainSettings(new X509Certificate2Collection
                                                       {
                                                           rootA
                                                       },
                                                       X509RevocationMode.NoCheck,
                                                       TimeSpan.Zero,
                                                       new X509Certificate2Collection
                                                       {
                                                           outer,
                                                           inner
                                                       });
        var builtWithoutSibling = TimestampTokenValidation.BuildChain(token, signer, settingsWithoutSibling);
        TimestampTokenValidation.MatchesTrustedChain(token, signer, builtWithoutSibling.Certificates,
                                                     new X509Certificate2Collection
                                                     {
                                                         signer,
                                                         inner,
                                                         outer,
                                                         rootA
                                                     }, settingsWithoutSibling)
                                .Should()
                                .BeTrue("without any sibling offered, both recorded positions are genuinely unique");

        var settings = new ChainSettings(new X509Certificate2Collection
                                         {
                                             rootA,
                                             rootB
                                         },
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection
                                         {
                                             outer,
                                             inner,
                                             outerSibling
                                         });

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        built.Certificates.Select(Convert.ToHexString)
             .Should()
             .Equal(Convert.ToHexString(signer.RawData), Convert.ToHexString(inner.RawData), Convert.ToHexString(outer.RawData), Convert.ToHexString(rootA.RawData));
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates,
                                                     new X509Certificate2Collection
                                                     {
                                                         signer,
                                                         inner,
                                                         outer,
                                                         rootA
                                                     }, settings)
                                .Should()
                                .BeFalse(
                                    "the outer intermediate's shared-key sibling reaches its own independently trusted root directly, even though the inner position has no sibling of its own");
    }

    [Test]
    public async Task MatchesTrustedChain_UnrelatedCertificateSharingDirectIssuerNameInsertedBeforeIt_IsRejected()
    {
        using var authority = new TestTimestampAuthority();
        var (token, signer) = await StampAsync(authority);
        using var rootKey = authority.Root.GetECDsaPrivateKey()!;
        // decoy shares the root's own subject name and is genuinely issued by the root, but it never issued the
        // signer: the signer was issued directly by the root itself.
        var (decoy, _) = CreateIntermediate(authority.Root.Subject, authority.Root, rootKey);
        var settings = new ChainSettings(new X509Certificate2Collection(authority.Root), X509RevocationMode.NoCheck, TimeSpan.Zero, new X509Certificate2Collection());
        var tamperedStored = new X509Certificate2Collection
        {
            signer,
            decoy,
            authority.Root
        };

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        built.Certificates.Should().HaveCount(2, "the signer is issued directly by the root, with no genuine certificate between them");
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, tamperedStored, settings)
                                .Should()
                                .BeFalse("the recorded chain inserts a certificate that was never actually part of the signer's real path");
    }

    [Test]
    public void MatchesTrustedChain_CounterfeitIssuerGroupEmbeddedInToken_IgnoresItAndAccepts()
    {
        // The token's embedded certificates are not covered by its signature. A counterfeit intermediate carrying
        // the genuine intermediate's subject and key is issued by a group of same-name, same-key certificates that
        // all issue one another, so excluding the genuine intermediate would walk every ordering of the group
        // (factorial in its size) unless certificates that can never reach a trusted root are pruned first.
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Counterfeit Group Root");
        var (intermediate, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Counterfeit Group Intermediate", root, rootKey);
        var signer = CreateSigner("CN=Chaos.Mongo Counterfeit Group Signer", intermediate, intermediateKey);
        var (groupRoot, groupKey) = CreateRoot("CN=Chaos.Mongo Counterfeit Group Member");
        var counterfeit = CreateCrossSigned(intermediate, groupRoot, groupKey);
        var group = Enumerable.Range(0, 12).Select(_ => CreateCrossSigned(groupRoot, groupRoot, groupKey)).Prepend(groupRoot).ToList();
        var token = CreateToken(signer, SHA256.HashData(Guid.NewGuid().ToByteArray()), extraCertificates: [intermediate, counterfeit, .. group]);
        var stored = new X509Certificate2Collection
        {
            signer,
            intermediate,
            root
        };
        var settings = new ChainSettings(new X509Certificate2Collection(root), X509RevocationMode.NoCheck, TimeSpan.Zero, new X509Certificate2Collection());

        var verification = Task.Run(() => TimestampTokenValidation.MatchesTrustedChain(token,
                                                                                       signer,
                                                                                       stored.Select(certificate => certificate.RawData).ToList(),
                                                                                       stored,
                                                                                       settings));

        verification.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue("the counterfeit group can never reach the trusted root, so it is pruned before the search");
        verification.Result.Should().BeTrue("the counterfeit intermediate offers no alternate path to trust");
    }

    [Test]
    public void MatchesTrustedChain_MoreAlternatePathsThanTheSearchBound_FailsClosed()
    {
        var (rootA, rootAKey) = CreateRoot("CN=Chaos.Mongo Search Bound Root A");
        var (rootB, rootBKey) = CreateRoot("CN=Chaos.Mongo Search Bound Root B");
        var (intermediateA, intermediateKey) = CreateIntermediate("CN=Chaos.Mongo Search Bound Intermediate", rootA, rootAKey);
        // intermediateB's only path to trust is extended-key-usage-ineligible, so it is a candidate the search must
        // examine without it proving the recorded position non-unique.
        var intermediateB = CreateCrossSigned(intermediateA, rootB, rootBKey, extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"]);
        var signer = CreateSigner("CN=Chaos.Mongo Search Bound Signer", intermediateA, intermediateKey);
        var token = CreateToken(signer);
        var stored = new X509Certificate2Collection
        {
            signer,
            intermediateA,
            rootA
        };
        var settings = new ChainSettings(new X509Certificate2Collection
                                         {
                                             rootA,
                                             rootB
                                         },
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection
                                         {
                                             intermediateA,
                                             intermediateB,
                                             rootB
                                         });
        var built = stored.Select(certificate => certificate.RawData).ToList();

        TimestampTokenValidation.MatchesTrustedChain(token, signer, built, stored, settings)
                                .Should()
                                .BeTrue("within the bound, the ineligible candidate leaves the recorded position unique");
        TimestampTokenValidation.MatchesTrustedChain(token, signer, built, stored, settings, maximumCandidatePaths: 0)
                                .Should()
                                .BeFalse("a search that would examine more candidates than its bound fails closed");
    }

    [Test]
    public void MatchesTrustedChain_StoredRootByteIdenticalToConfiguredRoot_IsAcceptedWithoutCheckingItsSelfSignature()
    {
        // A configured root is trusted as configured, so a genuine trusted root whose self-signature the direct
        // signature check rejects (such as a SHA-1 one) keeps working; a corrupted self-signature stands in for it.
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Configured Root");
        var signer = CreateSigner("CN=Chaos.Mongo Configured Root Signer", root, rootKey);
        var rootBytes = root.RawData.ToArray();
        rootBytes[^1] ^= 0x01;
        using var configured = X509CertificateLoader.LoadCertificate(rootBytes);
        var token = CreateToken(signer);
        var stored = new X509Certificate2Collection
        {
            signer,
            configured
        };
        var settings = new ChainSettings(new X509Certificate2Collection(configured), X509RevocationMode.NoCheck, TimeSpan.Zero, new X509Certificate2Collection());

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, stored, settings)
                                .Should()
                                .BeTrue("the stored root is byte-identical to the configured one");
    }

    [Test]
    public void MatchesTrustedChain_StoredRootWithCorruptedSelfSignature_IsRejected()
    {
        // The corrupted root keeps the genuine root's subject and key, so the signer still links to it and to the
        // configured root alike; only its own self-signature no longer verifies.
        var (root, rootKey) = CreateRoot("CN=Chaos.Mongo Corrupted Root");
        var signer = CreateSigner("CN=Chaos.Mongo Corrupted Root Signer", root, rootKey);
        var corruptedBytes = root.RawData.ToArray();
        corruptedBytes[^1] ^= 0x01;
        using var corrupted = X509CertificateLoader.LoadCertificate(corruptedBytes);
        var token = CreateToken(signer);
        var settings = new ChainSettings(new X509Certificate2Collection(root), X509RevocationMode.NoCheck, TimeSpan.Zero, new X509Certificate2Collection());
        var corruptedStored = new X509Certificate2Collection
        {
            signer,
            corrupted
        };

        var built = TimestampTokenValidation.BuildChain(token, signer, settings);

        TimestampTokenValidation.MatchesTrustedChain(token, signer, built.Certificates, corruptedStored, settings)
                                .Should()
                                .BeFalse("the stored root is neither the configured root nor validly self-signed");
    }

    internal static X509Certificate2 CreateCrossSigned(X509Certificate2 original,
                                                       X509Certificate2 newIssuer,
                                                       ECDsa newIssuerKey,
                                                       Int32? pathLengthConstraint = 0,
                                                       IReadOnlyList<String>? extendedKeyUsageOids = null,
                                                       DateTimeOffset? notBefore = null,
                                                       DateTimeOffset? notAfter = null,
                                                       Byte[]? subjectKeyIdentifier = null)
    {
        using var publicKey = original.GetECDsaPublicKey()!;
        var request = new CertificateRequest(original.SubjectName, publicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, pathLengthConstraint is not null, pathLengthConstraint ?? 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        if (extendedKeyUsageOids is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(ToOidCollection(extendedKeyUsageOids), true));
        }

        if (subjectKeyIdentifier is not null)
        {
            // An explicit identifier, not derived from the (shared) key, so otherwise cryptographically identical
            // siblings can still be told apart by a chain engine that prefers a Subject/Authority Key Identifier
            // match over subject name alone (see CreateSignerWithKeyIdentifier/MatchesTrustedChain_CertificateMultisetReordered_IsRejected).
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(subjectKeyIdentifier, false));
        }

        return request.Create(newIssuer.SubjectName,
                              X509SignatureGenerator.CreateForECDsa(newIssuerKey),
                              notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
                              notAfter ?? DateTimeOffset.UtcNow.AddDays(1),
                              RandomNumberGenerator.GetBytes(8));
    }

    internal static (X509Certificate2 Certificate, ECDsa Key) CreateIntermediate(String subjectName,
                                                                                 X509Certificate2 issuer,
                                                                                 ECDsa issuerKey,
                                                                                 Int32? pathLengthConstraint = 0,
                                                                                 DateTimeOffset? notBefore = null,
                                                                                 DateTimeOffset? notAfter = null,
                                                                                 Byte[]? nameConstraints = null,
                                                                                 IReadOnlyList<String>? extendedKeyUsageOids = null,
                                                                                 Byte[]? subjectKeyIdentifier = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, pathLengthConstraint is not null, pathLengthConstraint ?? 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        if (nameConstraints is not null)
        {
            request.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, nameConstraints, true));
        }

        if (extendedKeyUsageOids is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(ToOidCollection(extendedKeyUsageOids), true));
        }

        if (subjectKeyIdentifier is not null)
        {
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(subjectKeyIdentifier, false));
        }

        using var issued = request.Create(issuer.SubjectName,
                                          X509SignatureGenerator.CreateForECDsa(issuerKey),
                                          notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
                                          notAfter ?? DateTimeOffset.UtcNow.AddDays(1),
                                          RandomNumberGenerator.GetBytes(8));
        return (issued.CopyWithPrivateKey(key), ECDsa.Create(key.ExportParameters(true)));
    }

    /// <summary>
    /// An intermediate carrying a Subject Key Identifier and an Authority Key Identifier naming its issuer's own
    /// (RFC 5280, 4.2.1.1/4.2.1.2), as a genuine, reissued CA certificate would — letting the chain engine
    /// disambiguate same-named candidates by key identifier rather than subject name alone.
    /// </summary>
    internal static (X509Certificate2 Certificate, ECDsa Key) CreateIntermediateWithKeyIdentifier(String subjectName,
                                                                                                  X509Certificate2 issuer,
                                                                                                  ECDsa issuerKey)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var issued = request.Create(issuer.SubjectName,
                                          X509SignatureGenerator.CreateForECDsa(issuerKey),
                                          DateTimeOffset.UtcNow.AddDays(-1),
                                          DateTimeOffset.UtcNow.AddDays(1),
                                          RandomNumberGenerator.GetBytes(8));
        return (issued.CopyWithPrivateKey(key), ECDsa.Create(key.ExportParameters(true)));
    }

    internal static (X509Certificate2 Certificate, ECDsa Key) CreateRoot(String subjectName,
                                                                         Byte[]? nameConstraints = null,
                                                                         DateTimeOffset? notBefore = null,
                                                                         DateTimeOffset? notAfter = null,
                                                                         Int32? pathLengthConstraint = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, pathLengthConstraint is not null, pathLengthConstraint ?? 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        if (nameConstraints is not null)
        {
            request.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, nameConstraints, true));
        }

        var certificate = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(1));
        return (certificate, ECDsa.Create(key.ExportParameters(true)));
    }

    /// <summary>
    /// A self-signed root carrying a Subject Key Identifier (RFC 5280, 4.2.1.2), as a genuine root certificate
    /// would, so an issued certificate can carry a matching Authority Key Identifier.
    /// </summary>
    internal static (X509Certificate2 Certificate, ECDsa Key) CreateRootWithKeyIdentifier(String subjectName, Byte[]? nameConstraints = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (nameConstraints is not null)
        {
            request.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, nameConstraints, true));
        }

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return (certificate, ECDsa.Create(key.ExportParameters(true)));
    }

    internal static X509Certificate2 CreateSigner(String subjectName,
                                                  X509Certificate2 issuer,
                                                  ECDsa issuerKey,
                                                  DateTimeOffset? notBefore = null,
                                                  DateTimeOffset? notAfter = null,
                                                  Byte[]? issuerSubjectKeyIdentifier = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(TimestampTokenValidation.TimeStampingPurposeOid)
        }, true));
        if (issuerSubjectKeyIdentifier is not null)
        {
            // Names the issuer's explicit Subject Key Identifier directly, so a chain engine that prefers an
            // identifier match over subject name alone resolves to that one issuer even among other, otherwise
            // cryptographically identical (same subject, same key) candidates carrying a different identifier.
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(issuerSubjectKeyIdentifier));
        }

        using var issued = request.Create(issuer.SubjectName,
                                          X509SignatureGenerator.CreateForECDsa(issuerKey),
                                          notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
                                          notAfter ?? DateTimeOffset.UtcNow.AddDays(1),
                                          RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// A signer carrying an Authority Key Identifier naming its issuer, matching
    /// <see cref="CreateIntermediateWithKeyIdentifier"/>.
    /// </summary>
    internal static X509Certificate2 CreateSignerWithKeyIdentifier(String subjectName, X509Certificate2 issuer, ECDsa issuerKey)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(TimestampTokenValidation.TimeStampingPurposeOid)
        }, true));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var issued = request.Create(issuer.SubjectName,
                                          X509SignatureGenerator.CreateForECDsa(issuerKey),
                                          DateTimeOffset.UtcNow.AddDays(-1),
                                          DateTimeOffset.UtcNow.AddDays(1),
                                          RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// Builds a minimal, decodable RFC 3161 token signed by <paramref name="signer"/>, as a signature-only fixture:
    /// the digest it names is arbitrary, since the tests calling this exercise chain validation, not imprint checks.
    /// </summary>
    internal static Rfc3161TimestampToken CreateToken(X509Certificate2 signer, DateTimeOffset? time = null)
        => CreateToken(signer, SHA256.HashData(Guid.NewGuid().ToByteArray()), time);

    /// <summary>
    /// Builds a minimal, decodable RFC 3161 token signed by <paramref name="signer"/> over <paramref name="digest"/>,
    /// embedding <paramref name="extraCertificates"/> alongside the signer in the token's own certificate set (as a
    /// genuine TSA-supplied bridging or cross-signing certificate would travel with the stored token bytes).
    /// </summary>
    internal static Rfc3161TimestampToken CreateToken(X509Certificate2 signer,
                                                      Byte[] digest,
                                                      DateTimeOffset? time = null,
                                                      IReadOnlyList<X509Certificate2>? extraCertificates = null)
    {
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5.6"), new Oid("2.16.840.1.101.3.4.2.1"), digest, new Byte[] { 0x01 }, time ?? DateTimeOffset.UtcNow);
        var signedCms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signer)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
        };
        foreach (var extra in extraCertificates ?? [])
        {
            cmsSigner.Certificates.Add(extra);
        }

        // RFC 3161 requires the signing-certificate attribute (RFC 5035, ESSCertIDv2).
        var essCertId = new AsnWriter(AsnEncodingRules.DER);
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        {
            essCertId.WriteOctetString(SHA256.HashData(signer.RawData));
        }

        cmsSigner.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", essCertId.Encode()));
        signedCms.ComputeSignature(cmsSigner);
        Rfc3161TimestampToken.TryDecode(signedCms.Encode(), out var token, out _).Should().BeTrue();
        return token ?? throw new AssertionException("The constructed token does not decode.");
    }

    /// <summary>
    /// Encodes an X.509 <c>NameConstraints</c> extension value (RFC 5280, section 4.2.1.10) with at most one
    /// <c>directoryName</c> general name per permitted/excluded subtree.
    /// </summary>
    internal static Byte[] EncodeNameConstraints(X509Certificate2? permitted = null, X509Certificate2? excluded = null)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            if (permitted is not null)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    WriteDirectoryNameSubtree(writer, permitted);
                }
            }

            if (excluded is not null)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                {
                    WriteDirectoryNameSubtree(writer, excluded);
                }
            }
        }

        return writer.Encode();
    }

    private static async Task<(Rfc3161TimestampToken Token, X509Certificate2 Signer)> StampAsync(TestTimestampAuthority authority)
    {
        var digest = SHA256.HashData(Guid.NewGuid().ToByteArray());
        var result = await authority.CreateAuthority().TimestampAsync(digest, HashAlgorithmName.SHA256, CancellationToken.None);
        Rfc3161TimestampToken.TryDecode(result.Token, out var token, out _).Should().BeTrue();
        return (token ?? throw new AssertionException("The authority's token does not decode."), authority.SigningCertificate);
    }

    private static OidCollection ToOidCollection(IReadOnlyList<String> oids)
    {
        var collection = new OidCollection();
        foreach (var oid in oids)
        {
            collection.Add(new Oid(oid));
        }

        return collection;
    }

    private static void WriteDirectoryNameSubtree(AsnWriter writer, X509Certificate2 certificate)
    {
        // GeneralSubtree ::= SEQUENCE { base GeneralName, minimum [0] DEFAULT 0, maximum [1] OPTIONAL }; this writes
        // only base, as a directoryName [4] general name naming the certificate's subject.
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4)))
            {
                writer.WriteEncodedValue(certificate.SubjectName.RawData);
            }
        }
    }
}
