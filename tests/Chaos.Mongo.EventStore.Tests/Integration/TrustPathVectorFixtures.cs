// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Builds the genuine certificates of the §5 trust-path scenarios of the tamper-evidence specification, so
/// <see cref="TamperEvidenceVectorGenerator"/> can stamp each over the real digest of a committed anchor and commit
/// them as shared anchors verified through the production <c>StoredTimestampVerifier.Verify</c>/<c>verify_timestamp</c>
/// paths, not only through the lower-level helpers <see cref="TrustedChainValidationTests"/> already covers.
/// </summary>
/// <remarks>
/// Each certificate uses a freshly generated key, so a scenario's certificates must be built exactly once and the
/// token built from that same <see cref="Scenario.Signer"/>/<see cref="Scenario.ExtraEmbeddedCertificates"/> pair;
/// building the scenario twice (for example once to read its name ahead of a token and once for the token itself)
/// would record a chain whose trusted roots and stored chain belong to a different, unrelated set of certificates
/// than the token actually resolves to.
/// </remarks>
internal static class TrustPathVectorFixtures
{
    internal static Scenario BuildAlreadyExpiredAtTokenTimeRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Expired Window Root", notBefore: now.AddDays(-90), notAfter: now.AddDays(-25));
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Expired Window Signer", root, rootKey, now.AddDays(-60), now.AddDays(-10));
        return new Scenario("trust-chain-expired-at-token-time-rejected", signer, [], now.AddDays(-20), [signer, root], [root]);
    }

    /// <summary>
    /// Item 7 generalized to the token embedding both shared-key siblings of the recorded position, with the stored
    /// chain recording whichever candidate <see cref="TimestampTokenValidation.BuildChain"/> itself resolves (so the
    /// prefix comparison trivially passes): still rejected once the uniqueness search finds the *other*, unselected
    /// sibling's own path to its own, independently trusted root.
    /// </summary>
    internal static Scenario BuildBothSiblingsEmbeddedNonUniqueRejected()
    {
        var (rootA, rootAKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Both-Siblings Root A");
        var (rootB, rootBKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Both-Siblings Root B");
        var (intermediateA, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Both-Siblings Intermediate", rootA, rootAKey);
        var intermediateB = TrustedChainValidationTests.CreateCrossSigned(intermediateA, rootB, rootBKey);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Both-Siblings Signer", intermediateA, intermediateKey);
        IReadOnlyList<X509Certificate2> extraEmbedded = [intermediateA, intermediateB];
        IReadOnlyList<X509Certificate2> trustedRoots = [rootA, rootB];
        var storedChain = ResolveBuiltChain(signer, trustedRoots, extraEmbedded);
        return new Scenario("trust-both-siblings-embedded-non-unique-rejected", signer, extraEmbedded, DateTimeOffset.UtcNow, storedChain, trustedRoots);
    }

    internal static Scenario BuildBridgingPathAccepted()
    {
        var (rootA, rootAKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Bridge Root A");
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Bridge Signer", rootA, rootAKey);
        var (rootB, rootBKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Bridge Root B");
        var bridge = TrustedChainValidationTests.CreateCrossSigned(rootA, rootB, rootBKey);
        return new Scenario("trust-bridging-path-accepted", signer, [bridge], DateTimeOffset.UtcNow, [signer, rootA], [rootB]);
    }

    internal static Scenario BuildExtendedKeyUsageConstraintRejected()
    {
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector EKU Root");
        var (intermediate, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector EKU Intermediate",
                                                                                             root,
                                                                                             rootKey,
                                                                                             extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"]);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector EKU Signer", intermediate, intermediateKey);
        return new Scenario("trust-extended-key-usage-constraint-rejected", signer, [], DateTimeOffset.UtcNow, [signer, intermediate, root], [root]);
    }

    internal static Scenario BuildInsertedCertificateRejected()
    {
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Inserted Root");
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Inserted Signer", root, rootKey);
        var (decoy, _) = TrustedChainValidationTests.CreateIntermediate(root.Subject, root, rootKey);
        return new Scenario("trust-inserted-certificate-rejected", signer, [], DateTimeOffset.UtcNow, [signer, decoy, root], [root]);
    }

    internal static Scenario BuildNameConstraintRejected()
    {
        const String signerSubjectName = "CN=Chaos.Mongo Vector Name Constraint Signer";
        using var placeholder = TrustedChainValidationTests.CreateRoot(signerSubjectName).Certificate;
        var nameConstraints = TrustedChainValidationTests.EncodeNameConstraints(excluded: placeholder);
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Name Constraint Root", nameConstraints);
        var (intermediate, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Name Constraint Intermediate", root, rootKey);
        var signer = TrustedChainValidationTests.CreateSigner(signerSubjectName, intermediate, intermediateKey);
        return new Scenario("trust-name-constraint-rejected", signer, [], DateTimeOffset.UtcNow, [signer, intermediate, root], [root]);
    }

    internal static Scenario BuildNonFinalSelfIssuedIntermediateAccepted()
    {
        const String sharedName = "CN=Chaos.Mongo Vector Reissued CA";
        using var placeholder = TrustedChainValidationTests.CreateRoot(sharedName).Certificate;
        var nameConstraints = TrustedChainValidationTests.EncodeNameConstraints(excluded: placeholder);
        var (root, rootKey) = TrustedChainValidationTests.CreateRootWithKeyIdentifier(sharedName, nameConstraints);
        var (reissued, reissuedKey) = TrustedChainValidationTests.CreateIntermediateWithKeyIdentifier(sharedName, root, rootKey);
        var signer = TrustedChainValidationTests.CreateSignerWithKeyIdentifier("CN=Chaos.Mongo Vector Reissued CA Signer", reissued, reissuedKey);
        return new Scenario("trust-non-final-self-issued-intermediate-accepted", signer, [], DateTimeOffset.UtcNow, [signer, reissued, root], [root]);
    }

    /// <summary>
    /// Item 5's cross-certificate variant: a certificate sharing the trusted root's own subject and key (a genuine
    /// re-issuance of the root, cryptographically indistinguishable from it except for its own, stricter
    /// <see cref="X509BasicConstraintsExtension"/>) is inserted before a path-length-restricted intermediate. Each
    /// individual recorded link is genuine (the cross-certificate's signature is the root's own key; the
    /// intermediate's is the cross-certificate's), but the engine's own shortest-path resolution naturally skips the
    /// redundant cross-certificate in favor of the directly configured root, so the exact-depth-count/prefix check
    /// that pins the complete recorded path — not a path-length chain-building exception specifically — is what
    /// rejects it, proving the pinning check catches a redundant link a plain shortest-path resolution would
    /// silently skip.
    /// </summary>
    internal static Scenario BuildPathLengthConstraintCrossCertificateVariantRejected()
    {
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Path Length Cross-Certificate Root");
        var crossCertificate = TrustedChainValidationTests.CreateCrossSigned(root, root, rootKey, 0);
        var (intermediate, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Path Length Cross-Certificate Intermediate",
                                                                                             crossCertificate, rootKey, 0);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Path Length Cross-Certificate Signer", intermediate, intermediateKey);
        return new Scenario("trust-path-length-cross-certificate-variant-rejected", signer, [crossCertificate], DateTimeOffset.UtcNow,
                            [signer, intermediate, crossCertificate, root], [root]);
    }

    internal static Scenario BuildPathLengthConstraintRejected()
    {
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Path Length Root", pathLengthConstraint: 0);
        var (intermediate, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Path Length Intermediate", root, rootKey, 0);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Path Length Signer", intermediate, intermediateKey);
        return new Scenario("trust-path-length-constraint-rejected", signer, [], DateTimeOffset.UtcNow, [signer, intermediate, root], [root]);
    }

    internal static Scenario BuildReorderedMultisetRejected()
    {
        const String sharedName = "CN=Chaos.Mongo Vector Reorder Twin";
        var (root, rootKey) = TrustedChainValidationTests.CreateRootWithKeyIdentifier("CN=Chaos.Mongo Vector Reorder Root");
        var (intermediateA, intermediateAKey) = TrustedChainValidationTests.CreateIntermediateWithKeyIdentifier(sharedName, root, rootKey);
        var (intermediateB, intermediateBKey) = TrustedChainValidationTests.CreateIntermediateWithKeyIdentifier(sharedName, intermediateA, intermediateAKey);
        var signer = TrustedChainValidationTests.CreateSignerWithKeyIdentifier("CN=Chaos.Mongo Vector Reorder Signer", intermediateB, intermediateBKey);

        // The genuine order the engine resolves (by key identifier, despite the shared subject name) is
        // signer -> intermediateB -> intermediateA -> root; recording intermediateA and intermediateB swapped is the
        // same multiset in an order inconsistent with their actual issuer relationships.
        return new Scenario("trust-reordered-multiset-rejected", signer, [], DateTimeOffset.UtcNow, [signer, intermediateA, intermediateB, root], [root]);
    }

    /// <summary>
    /// Item 9's same-root masking case: a genuine, shared-key sibling of the recorded intermediate is reachable
    /// through a *different path to the same trusted root* (not a second, independently trusted root), so
    /// restricting the uniqueness search to only the one root the recorded path itself reaches cannot, by itself,
    /// separate the recorded position from its alternate — proving the search's per-candidate isolation, not merely
    /// which roots are configured, is what the uniqueness proof actually depends on. A second, constraint-ineligible
    /// sibling is also embedded, reachable through its own bridge back to the same root, but never itself a valid
    /// alternate: it stays embedded-but-unoffered in the one alternate the search actually accepts, proving that
    /// finding the genuine alternate does not depend on it being the only other embedded candidate, and that
    /// <see cref="TimestampTokenValidation.BuildIsolatedChain"/> does not leak it into the accepted candidate's own
    /// isolated build.
    /// </summary>
    internal static Scenario BuildSameRootSiblingNonUniqueRejected()
    {
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Same-Root Sibling Root");
        var (intermediate, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Same-Root Sibling Intermediate", root, rootKey);
        // Each bridge must itself permit one subordinate CA below it (its own cross-signed sibling), unlike the
        // recorded intermediate's own default pathLenConstraint of 0.
        var (bridge, bridgeKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Same-Root Sibling Bridge", root, rootKey, 1);
        // The sibling shares the recorded intermediate's own subject and key, but is reached through a different
        // intermediate (bridge) that itself leads back to the very same trusted root.
        var sibling = TrustedChainValidationTests.CreateCrossSigned(intermediate, bridge, bridgeKey);
        var (ineligibleBridge, ineligibleBridgeKey) =
            TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Same-Root Sibling Ineligible Bridge", root, rootKey, 1);
        // A second, also shared-key sibling, reached through its own bridge back to the same root, but itself
        // extended-key-usage-ineligible for timestamping: a genuine candidate the search must still consider and
        // reject on its own merits, embedded alongside the one genuine alternate without being part of it.
        var ineligibleSibling = TrustedChainValidationTests.CreateCrossSigned(intermediate, ineligibleBridge, ineligibleBridgeKey,
                                                                              extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"]);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Same-Root Sibling Signer", intermediate, intermediateKey);
        return new Scenario("trust-shared-key-sibling-non-unique-rejected-same-root", signer, [bridge, sibling, ineligibleBridge, ineligibleSibling],
                            DateTimeOffset.UtcNow, [signer, intermediate, root], [root]);
    }

    internal static Scenario BuildSharedKeySiblingIneligibleAlternateAccepted()
    {
        var (rootA, rootAKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Ineligible Sibling Root A");
        var (rootB, rootBKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Ineligible Sibling Root B");
        var (intermediateA, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Ineligible Sibling Intermediate", rootA, rootAKey);
        var intermediateB = TrustedChainValidationTests.CreateCrossSigned(intermediateA, rootB, rootBKey, extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"]);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Ineligible Sibling Signer", intermediateA, intermediateKey);
        return new Scenario("trust-shared-key-sibling-ineligible-alternate-accepted", signer, [intermediateA, intermediateB], DateTimeOffset.UtcNow,
                            [signer, intermediateA, rootA], [rootA, rootB]);
    }

    internal static Scenario BuildSharedKeySiblingNonUniqueRejected()
    {
        var (rootA, rootAKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Sibling Root A");
        var (rootB, rootBKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Sibling Root B");
        var (intermediateA, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Sibling Intermediate", rootA, rootAKey);
        var intermediateB = TrustedChainValidationTests.CreateCrossSigned(intermediateA, rootB, rootBKey);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Sibling Signer", intermediateA, intermediateKey);
        return new Scenario("trust-shared-key-sibling-non-unique-rejected", signer, [intermediateB], DateTimeOffset.UtcNow, [signer, intermediateA, rootA], [rootA, rootB]);
    }

    internal static Scenario BuildSubstitutedRootRejected()
    {
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Substituted Root");
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Substituted Root Signer", root, rootKey);
        var (decoyRoot, _) = TrustedChainValidationTests.CreateRoot(root.Subject);
        return new Scenario("trust-substituted-root-rejected", signer, [], DateTimeOffset.UtcNow, [signer, decoyRoot], [root]);
    }

    /// <summary>
    /// Item 8's three-sibling generalization: two alternates to the recorded intermediate position, each itself
    /// constraint-ineligible for a different reason (extended key usage; a name constraint on its own root), proving
    /// the uniqueness search checks every alternate rather than stopping once it has ruled out the first one it
    /// happens to try. Three same-subject, same-key candidates make .NET's own
    /// <see cref="TimestampTokenValidation.BuildChain"/>
    /// resolution of "the" chain non-deterministic across fresh keys (unlike the two-sibling case above, where either
    /// resolution still lets the uniqueness search reject the other candidate): a sibling's own Subject Key
    /// Identifier, not derived from the shared key, lets the signer's Authority Key Identifier name the recorded
    /// intermediate specifically, so the chain-building engine resolves it deterministically -- the same
    /// identifier-based disambiguation <see cref="BuildReorderedMultisetRejected"/> already relies on. This affects
    /// only chain building: <see cref="DirectSignatureVerification.IsDirectIssuer"/>, which the uniqueness search's
    /// alternate-path discovery depends on, matches by issuer name and signature alone, never by key identifier, so
    /// the search still discovers and rejects both siblings on their own merits.
    /// </summary>
    internal static Scenario BuildThreeSharedKeySiblingsEachConstraintIneligibleAccepted()
    {
        var (rootA, rootAKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Three Sibling Root A");
        var (rootB, rootBKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Three Sibling Root B");
        const String signerSubjectName = "CN=Chaos.Mongo Vector Three Sibling Signer";
        using var placeholder = TrustedChainValidationTests.CreateRoot(signerSubjectName).Certificate;
        var nameConstraints = TrustedChainValidationTests.EncodeNameConstraints(excluded: placeholder);
        var (rootC, rootCKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Three Sibling Root C", nameConstraints);
        var intermediateASubjectKeyIdentifier = RandomNumberGenerator.GetBytes(20);
        var (intermediateA, intermediateKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Three Sibling Intermediate", rootA, rootAKey,
                                                                                              subjectKeyIdentifier: intermediateASubjectKeyIdentifier);
        var intermediateB = TrustedChainValidationTests.CreateCrossSigned(intermediateA, rootB, rootBKey, extendedKeyUsageOids: ["1.3.6.1.5.5.7.3.2"],
                                                                          subjectKeyIdentifier: RandomNumberGenerator.GetBytes(20));
        var intermediateC = TrustedChainValidationTests.CreateCrossSigned(intermediateA, rootC, rootCKey, subjectKeyIdentifier: RandomNumberGenerator.GetBytes(20));
        var signer = TrustedChainValidationTests.CreateSigner(signerSubjectName, intermediateA, intermediateKey,
                                                              issuerSubjectKeyIdentifier: intermediateASubjectKeyIdentifier);
        return new Scenario("trust-three-shared-key-siblings-each-constraint-ineligible-accepted", signer, [intermediateA, intermediateB, intermediateC],
                            DateTimeOffset.UtcNow, [signer, intermediateA, rootA], [rootA, rootB, rootC]);
    }

    /// <summary>
    /// Item 7 generalized to a chain with two recorded intermediates, where only the outer one has a shared-key
    /// sibling reachable through a different, independently trusted root directly (not through any further bridge):
    /// proves the inner position, which has no sibling of its own and is the only certificate every path can reach
    /// the signer through, stays silent while the affected outer position alone causes rejection. A sibling placed
    /// at the inner position instead would let its own alternate path bypass outer entirely, so excluding either
    /// position would find the same alternate and never actually isolate which one is unique. Promotes
    /// <see
    ///     cref="TrustedChainValidationTests.MatchesTrustedChain_TwoRecordedIntermediatesOneWithSiblingReachableThroughAnotherRoot_IsRejected"/>
    /// to a shared, production-path vector.
    /// </summary>
    internal static Scenario BuildTwoIntermediateSiblingRejected()
    {
        var (rootA, rootAKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Two-Intermediate Root A");
        var (rootB, rootBKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Two-Intermediate Root B");
        var (outer, outerKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Two-Intermediate Outer", rootA, rootAKey, 1);
        var (inner, innerKey) = TrustedChainValidationTests.CreateIntermediate("CN=Chaos.Mongo Vector Two-Intermediate Inner", outer, outerKey);
        var outerSibling = TrustedChainValidationTests.CreateCrossSigned(outer, rootB, rootBKey, 1);
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Two-Intermediate Signer", inner, innerKey);
        return new Scenario("trust-two-intermediate-sibling-rejected", signer, [outerSibling], DateTimeOffset.UtcNow,
                            [signer, inner, outer, rootA], [rootA, rootB]);
    }

    internal static Scenario BuildValidOnlyAtTokenTimeAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var validFrom = now.AddDays(-60);
        var validUntil = now.AddDays(-30);
        var (root, rootKey) = TrustedChainValidationTests.CreateRoot("CN=Chaos.Mongo Vector Time Window Root", notBefore: now.AddDays(-90), notAfter: now.AddDays(10));
        var signer = TrustedChainValidationTests.CreateSigner("CN=Chaos.Mongo Vector Time Window Signer", root, rootKey, validFrom, validUntil);
        return new Scenario("trust-valid-only-at-token-time-accepted", signer, [], now.AddDays(-45), [signer, root], [root]);
    }

    /// <summary>
    /// Resolves the chain <see cref="TimestampTokenValidation.BuildChain"/> itself selects for a throwaway token of
    /// <paramref name="signer"/>, so a scenario can commit *that* result as its stored chain when the genuinely
    /// correct recorded path is ambiguous between several same-key candidates (whichever one the engine happens to
    /// resolve), rather than assuming one. Building this throwaway token does not affect the scenario's own, later
    /// token: path resolution depends only on the certificates and settings, never on the digest a token carries.
    /// </summary>
    private static IReadOnlyList<X509Certificate2> ResolveBuiltChain(X509Certificate2 signer,
                                                                     IReadOnlyList<X509Certificate2> trustedRoots,
                                                                     IReadOnlyList<X509Certificate2> extraCertificates)
    {
        var token = TrustedChainValidationTests.CreateToken(signer);
        var settings = new ChainSettings(new X509Certificate2Collection(trustedRoots.ToArray()),
                                         X509RevocationMode.NoCheck,
                                         TimeSpan.Zero,
                                         new X509Certificate2Collection(extraCertificates.ToArray()));
        var built = TimestampTokenValidation.BuildChain(token, signer, settings);
        return built.Certificates.Select(X509CertificateLoader.LoadCertificate).ToList();
    }

    /// <summary>
    /// One scenario's certificates: the signer a token must be built from, any further certificate the token's own
    /// certificate set must embed beside it (a bridging or shared-key sibling certificate), the time the token must
    /// assert, the chain recorded as evidence, and the roots that must be trusted to reproduce it.
    /// </summary>
    internal sealed record Scenario(
        String Name,
        X509Certificate2 Signer,
        IReadOnlyList<X509Certificate2> ExtraEmbeddedCertificates,
        DateTimeOffset TokenTime,
        IReadOnlyList<X509Certificate2> StoredChain,
        IReadOnlyList<X509Certificate2> TrustedRoots);
}
