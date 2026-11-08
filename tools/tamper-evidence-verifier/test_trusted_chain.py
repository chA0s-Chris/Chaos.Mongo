#!/usr/bin/env python3
"""
Regression tests for verify.py's Check A/Check B chain-trust validation (matches_trusted_chain and the primitives it
calls): the stored/received certificate chain must genuinely, uniquely reach a certificate that is actually a member
of the configured trusted roots today (Check A), and the stored chain's own recorded root must independently be
genuine, not merely configured (Check B). See docs/tamper-evidence-specification.md, item 9, and
ai-plans/0248-say-what-verification-actually-proved.md, Technical Details section 5.

Unlike verify.py itself (standard library only), these tests use the third-party `cryptography` package
(pip install cryptography) purely to build throwaway test certificates; nothing here is imported by verify.py or
shipped with it. Run with: python3 -m unittest tools/tamper-evidence-verifier/test_trusted_chain.py -v
"""
import datetime
import importlib.util
import os
import sys
import tempfile
import unittest

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

_HERE = os.path.dirname(os.path.abspath(__file__))
_SPEC = importlib.util.spec_from_file_location("verify", os.path.join(_HERE, "verify.py"))
verify = importlib.util.module_from_spec(_SPEC)
sys.path.insert(0, _HERE)
_SPEC.loader.exec_module(verify)

TIME_STAMPING_OID = "1.3.6.1.5.5.7.3.8"
CLIENT_AUTH_OID = "1.3.6.1.5.5.7.3.2"


def make_key():
    return ec.generate_private_key(ec.SECP256R1())


def make_rsa_key():
    return rsa.generate_private_key(public_exponent=65537, key_size=2048)


def make_certificate(subject_name, issuer_name, issuer_key, public_key, is_ca,
                      path_length=None, eku=None, not_before=None, not_after=None, name_constraints=None,
                      signing_algorithm=None, key_identifiers=False, aki_issuer=None):
    now = datetime.datetime.now(datetime.timezone.utc)
    builder = (x509.CertificateBuilder()
               .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, subject_name)]))
               .issuer_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, issuer_name)]))
               .public_key(public_key)
               .serial_number(x509.random_serial_number())
               .not_valid_before(not_before or (now - datetime.timedelta(days=1)))
               .not_valid_after(not_after or (now + datetime.timedelta(days=1)))
               .add_extension(x509.BasicConstraints(ca=is_ca, path_length=path_length), critical=True))
    if eku is not None:
        builder = builder.add_extension(x509.ExtendedKeyUsage(eku), critical=True)
    if name_constraints is not None:
        builder = builder.add_extension(name_constraints, critical=True)
    if key_identifiers:
        # RFC 5280, 4.2.1.1/4.2.1.2: a genuine, reissued CA certificate carries a Subject Key Identifier and an
        # Authority Key Identifier naming its issuer's own, so a path builder disambiguates same-named candidates
        # by key identifier rather than subject name alone.
        builder = builder.add_extension(x509.SubjectKeyIdentifier.from_public_key(public_key), critical=False)
        if aki_issuer is not None:
            issuer_ski = aki_issuer.extensions.get_extension_for_class(x509.SubjectKeyIdentifier).value
            builder = builder.add_extension(x509.AuthorityKeyIdentifier.from_issuer_subject_key_identifier(issuer_ski), critical=False)
    return builder.sign(private_key=issuer_key, algorithm=signing_algorithm or hashes.SHA256())


def der(certificate):
    return certificate.public_bytes(serialization.Encoding.DER)


class MatchesTrustedChainTests(unittest.TestCase):
    """Check A (genuine, unique current trust) and Check B (the stored chain's own root is independently genuine)."""

    def matches(self, chain, embedded, trusted, token_time=None):
        chain_fields = [verify.certificate_fields(c) for c in chain]
        with tempfile.TemporaryDirectory() as directory:
            return verify.matches_trusted_chain(chain, chain_fields, embedded, trusted,
                                                token_time or datetime.datetime.now(datetime.timezone.utc), directory)

    def test_unrelated_root_with_zero_relationship_to_trust_is_rejected(self):
        """F1 regression: the stored chain's own root has no cryptographic relationship whatsoever to the
        configured trusted root. Reproduced during code review (session 9286b60b): before the fix, Check A never
        required the primary/stored path to reach a genuinely configured trusted root at all, only an *alternate*
        path during the uniqueness search; a chain rooted in a completely unrelated, never-configured root was
        incorrectly accepted."""
        unrelated_root_key = make_key()
        unrelated_root = make_certificate("R2-UNRELATED-ROOT", "R2-UNRELATED-ROOT", unrelated_root_key, unrelated_root_key.public_key(), True)
        intermediate_key = make_key()
        intermediate = make_certificate("I-under-R2", "R2-UNRELATED-ROOT", unrelated_root_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_key()
        signer = make_certificate("Signer-under-R2", "I-under-R2", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])
        configured_root_key = make_key()
        configured_root = make_certificate("R1-CONFIGURED-TRUSTED-ROOT", "R1-CONFIGURED-TRUSTED-ROOT", configured_root_key,
                                           configured_root_key.public_key(), True)

        chain = [der(signer), der(intermediate), der(unrelated_root)]
        trusted = [der(configured_root)]

        self.assertFalse(self.matches(chain, [], trusted),
                         "the stored chain's root has zero relationship to the only configured trusted root")

    def test_chain_rooted_directly_at_a_configured_trusted_root_is_accepted(self):
        """Sanity: today's common, unaffected case — the stored chain's own final certificate is itself a
        configured trusted root."""
        root_key = make_key()
        root = make_certificate("Root", "Root", root_key, root_key.public_key(), True)
        intermediate_key = make_key()
        intermediate = make_certificate("Intermediate", "Root", root_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_key()
        signer = make_certificate("Signer", "Intermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])

        chain = [der(signer), der(intermediate), der(root)]
        trusted = [der(root)]

        self.assertTrue(self.matches(chain, [], trusted))

    def test_longer_bridge_to_a_second_configured_trusted_root_is_accepted(self):
        """The stored chain's own root is not itself configured as trusted, but a cross-signed bridge certificate
        (carried as one of the token's own embedded certificates) connects it to a second, configured trusted root;
        Check A's relaxed length permits continuing past the stored chain's own length."""
        root_key = make_key()
        root = make_certificate("Root", "Root", root_key, root_key.public_key(), True)
        intermediate_key = make_key()
        intermediate = make_certificate("Intermediate", "Root", root_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_key()
        signer = make_certificate("Signer", "Intermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])
        root_b_key = make_key()
        root_b = make_certificate("RootB", "RootB", root_b_key, root_b_key.public_key(), True)
        # Bridge: the same subject and key as root, re-issued by root_b.
        bridge = make_certificate("Root", "RootB", root_b_key, root_key.public_key(), True, path_length=1)

        chain = [der(signer), der(intermediate), der(root)]
        embedded = [der(bridge)]
        trusted = [der(root_b)]

        self.assertTrue(self.matches(chain, embedded, trusted))

    def test_substituted_root_sharing_subject_but_never_issuing_the_intermediate_is_rejected(self):
        """Check B: the stored chain records a decoy root sharing the genuine root's subject name but a different
        key, which never actually issued the intermediate above the signer."""
        root_key = make_key()
        root = make_certificate("Root", "Root", root_key, root_key.public_key(), True)
        intermediate_key = make_key()
        intermediate = make_certificate("Intermediate", "Root", root_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_key()
        signer = make_certificate("Signer", "Intermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])
        decoy_root_key = make_key()
        decoy_root = make_certificate("Root", "Root", decoy_root_key, decoy_root_key.public_key(), True)

        substituted_chain = [der(signer), der(intermediate), der(decoy_root)]
        trusted = [der(root), der(decoy_root)]

        self.assertFalse(self.matches(substituted_chain, [], trusted),
                         "the decoy root shares the genuine root's subject but never issued the intermediate")

    def test_shared_key_sibling_reachable_through_another_root_is_rejected_as_non_unique(self):
        """Check A's per-position uniqueness search: a genuine, shared-key sibling of the recorded intermediate is
        reachable through a different, independently trusted root, so the recorded position is not the only way to
        reach trust even though the recorded/primary path itself still validates."""
        root_a_key = make_key()
        root_a = make_certificate("RootA", "RootA", root_a_key, root_a_key.public_key(), True)
        root_b_key = make_key()
        root_b = make_certificate("RootB", "RootB", root_b_key, root_b_key.public_key(), True)
        intermediate_key = make_key()
        intermediate_a = make_certificate("SharedIntermediate", "RootA", root_a_key, intermediate_key.public_key(), True, path_length=0)
        # Sibling: same subject and key as intermediate_a, issued by root_b instead.
        intermediate_b = make_certificate("SharedIntermediate", "RootB", root_b_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_key()
        signer = make_certificate("Signer", "SharedIntermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])

        chain = [der(signer), der(intermediate_a), der(root_a)]
        embedded = [der(intermediate_b)]
        trusted = [der(root_a), der(root_b)]

        self.assertFalse(self.matches(chain, embedded, trusted),
                         "intermediate_b, sharing the recorded position's key and subject, reaches its own independently trusted root")

    def test_shared_key_sibling_whose_only_alternate_path_is_extended_key_usage_ineligible_is_accepted(self):
        """The only remaining alternate sibling's own path is itself constraint-ineligible (extended key usage
        excludes timestamping), so the recorded position is genuinely unique: the search must not over-reject."""
        root_a_key = make_key()
        root_a = make_certificate("RootA", "RootA", root_a_key, root_a_key.public_key(), True)
        root_b_key = make_key()
        root_b = make_certificate("RootB", "RootB", root_b_key, root_b_key.public_key(), True)
        intermediate_key = make_key()
        intermediate_a = make_certificate("SharedIntermediate", "RootA", root_a_key, intermediate_key.public_key(), True, path_length=0)
        # Sibling reaches root_b, but is ineligible for timestamping (clientAuth only).
        intermediate_b = make_certificate("SharedIntermediate", "RootB", root_b_key, intermediate_key.public_key(), True, path_length=0,
                                          eku=[x509.ObjectIdentifier(CLIENT_AUTH_OID)])
        signer_key = make_key()
        signer = make_certificate("Signer", "SharedIntermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])

        chain = [der(signer), der(intermediate_a), der(root_a)]
        embedded = [der(intermediate_b)]
        trusted = [der(root_a), der(root_b)]

        self.assertTrue(self.matches(chain, embedded, trusted),
                        "the only remaining alternate sibling is itself constraint-ineligible, so the recorded position is genuinely unique")

    def test_three_shared_key_siblings_each_constraint_ineligible_for_a_different_reason_is_accepted(self):
        """Generalizes the two-sibling case to three: one alternate is extended-key-usage-ineligible, the other's
        root excludes the signer's own name, proving the uniqueness search checks every alternate rather than
        stopping once it has ruled out the first one it happens to try."""
        root_a_key = make_key()
        root_a = make_certificate("RootA", "RootA", root_a_key, root_a_key.public_key(), True)
        root_b_key = make_key()
        root_b = make_certificate("RootB", "RootB", root_b_key, root_b_key.public_key(), True)
        signer_name = "Signer"
        excluded_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, signer_name)])
        constraints = x509.NameConstraints(permitted_subtrees=None, excluded_subtrees=[x509.DirectoryName(excluded_name)])
        root_c_key = make_key()
        root_c = make_certificate("RootC", "RootC", root_c_key, root_c_key.public_key(), True, name_constraints=constraints)
        intermediate_key = make_key()
        intermediate_a = make_certificate("SharedIntermediate", "RootA", root_a_key, intermediate_key.public_key(), True, path_length=0)
        # Sibling reaches root_b, but is ineligible for timestamping (clientAuth only).
        intermediate_b = make_certificate("SharedIntermediate", "RootB", root_b_key, intermediate_key.public_key(), True, path_length=0,
                                          eku=[x509.ObjectIdentifier(CLIENT_AUTH_OID)])
        # Sibling reaches root_c, whose own name constraint excludes the signer's name across the complete path.
        intermediate_c = make_certificate("SharedIntermediate", "RootC", root_c_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_key()
        signer = make_certificate(signer_name, "SharedIntermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])

        chain = [der(signer), der(intermediate_a), der(root_a)]
        embedded = [der(intermediate_b), der(intermediate_c)]
        trusted = [der(root_a), der(root_b), der(root_c)]

        self.assertTrue(self.matches(chain, embedded, trusted),
                        "both alternate siblings are themselves constraint-ineligible for different reasons")

    def test_certificate_multiset_reordered_is_rejected(self):
        """A chain listing the same multiset of certificates as a genuine path but in an order inconsistent with
        their actual issuer relationships, possible when two recorded certificates genuinely share a subject name,
        is rejected by the per-link issuer_signature_ok checks, even though a call that only counts path depth would
        accept the same multiset. Each certificate carries a Subject/Authority Key Identifier, as a genuine,
        reissued CA certificate would (RFC 5280, 4.2.1.1/4.2.1.2): without it, two adjacent certificates sharing one
        subject name make the inner one look self-issued by name to OpenSSL's own path builder regardless of order,
        but with it, the path builder disambiguates by key identifier instead, so the genuinely correct order is
        separately proven to validate here, not only the swapped order to be rejected."""
        root_key = make_key()
        root = make_certificate("Root", "Root", root_key, root_key.public_key(), True, key_identifiers=True)
        shared_name = "Twin"
        mid_key = make_key()
        mid = make_certificate(shared_name, "Root", root_key, mid_key.public_key(), True, path_length=1,
                               key_identifiers=True, aki_issuer=root)
        inner_key = make_key()
        inner = make_certificate(shared_name, shared_name, mid_key, inner_key.public_key(), True, path_length=0,
                                 key_identifiers=True, aki_issuer=mid)
        signer_key = make_key()
        signer = make_certificate("Signer", shared_name, inner_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)], key_identifiers=True, aki_issuer=inner)

        genuine_chain = [der(signer), der(inner), der(mid), der(root)]
        reordered_chain = [der(signer), der(mid), der(inner), der(root)]
        trusted = [der(root)]

        self.assertTrue(self.matches(genuine_chain, [], trusted),
                        "inner and mid are in their genuinely correct order")
        self.assertFalse(self.matches(reordered_chain, [], trusted),
                         "mid and inner are swapped, even though both certificates and the root are the same multiset")

    def test_non_final_self_issued_intermediate_exempt_from_roots_own_name_constraint_is_accepted(self):
        """The reissued certificate shares the root's own subject name and is issued by it, so it is self-issued by
        definition (RFC 5280, 4.2.1.10): issuer == subject. The root's own name constraint excludes that same
        subject name, which would reject the reissued certificate like any other non-self-issued certificate —
        except the self-issued, non-final exemption applies, since a signer still follows it in the complete path.
        Neither issuer_signature_ok (a direct signature/name check, with no RFC 5280 constraint propagation of its
        own) nor the prefix-preserving extension make this certificate a path's own final target, so the exemption
        reaches OpenSSL's whole-chain validation undisturbed."""
        shared_name = "ReissuedCA"
        excluded_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, shared_name)])
        constraints = x509.NameConstraints(permitted_subtrees=None, excluded_subtrees=[x509.DirectoryName(excluded_name)])
        root_key = make_key()
        root = make_certificate(shared_name, shared_name, root_key, root_key.public_key(), True,
                                name_constraints=constraints, key_identifiers=True)
        reissued_key = make_key()
        reissued = make_certificate(shared_name, shared_name, root_key, reissued_key.public_key(), True, path_length=0,
                                    key_identifiers=True, aki_issuer=root)
        signer_key = make_key()
        signer = make_certificate("Signer", shared_name, reissued_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)], key_identifiers=True, aki_issuer=reissued)

        chain = [der(signer), der(reissued), der(root)]
        trusted = [der(root)]

        self.assertTrue(self.matches(chain, [], trusted),
                        "the reissued certificate is self-issued and not the path's own final certificate, so the root's name constraint exempts it")


class VerifyRawSignatureTests(unittest.TestCase):
    """issuer_signature_ok's RSA PKCS #1 v1.5 path (matches_trusted_chain's other tests use only ECDSA keys): a
    prehashed pkeyutl verification that wraps the hash in a DigestInfo itself, rather than relying on pkeyutl's own
    -rawin/-digest convenience, which this tool's advertised OpenSSL 1.1.1 minimum does not have."""

    def matches(self, chain, embedded, trusted, token_time=None):
        chain_fields = [verify.certificate_fields(c) for c in chain]
        with tempfile.TemporaryDirectory() as directory:
            return verify.matches_trusted_chain(chain, chain_fields, embedded, trusted,
                                                token_time or datetime.datetime.now(datetime.timezone.utc), directory)

    def test_genuine_rsa_pkcs1_chain_is_accepted(self):
        root_key = make_rsa_key()
        root = make_certificate("RSA-Root", "RSA-Root", root_key, root_key.public_key(), True)
        intermediate_key = make_rsa_key()
        intermediate = make_certificate("RSA-Intermediate", "RSA-Root", root_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_rsa_key()
        signer = make_certificate("RSA-Signer", "RSA-Intermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])

        chain = [der(signer), der(intermediate), der(root)]
        trusted = [der(root)]

        self.assertTrue(self.matches(chain, [], trusted), "every link is a genuine RSA PKCS #1 v1.5 signature")

    def test_rsa_pkcs1_chain_with_tampered_intermediate_signature_is_rejected(self):
        root_key = make_rsa_key()
        root = make_certificate("RSA-Root", "RSA-Root", root_key, root_key.public_key(), True)
        intermediate_key = make_rsa_key()
        intermediate = make_certificate("RSA-Intermediate", "RSA-Root", root_key, intermediate_key.public_key(), True, path_length=0)
        signer_key = make_rsa_key()
        signer = make_certificate("RSA-Signer", "RSA-Intermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)])

        # The signature is the certificate DER's final bytes, so flipping the last byte corrupts only it.
        tampered_intermediate = bytearray(der(intermediate))
        tampered_intermediate[-1] ^= 0x01
        chain = [der(signer), bytes(tampered_intermediate), der(root)]
        trusted = [der(root)]

        self.assertFalse(self.matches(chain, [], trusted), "the intermediate's signature no longer verifies under the root's key")

    def rsa_pkcs1_chain(self, signing_algorithm):
        root_key = make_rsa_key()
        root = make_certificate("RSA-Root", "RSA-Root", root_key, root_key.public_key(), True,
                                signing_algorithm=signing_algorithm)
        intermediate_key = make_rsa_key()
        intermediate = make_certificate("RSA-Intermediate", "RSA-Root", root_key, intermediate_key.public_key(), True,
                                        path_length=0, signing_algorithm=signing_algorithm)
        signer_key = make_rsa_key()
        signer = make_certificate("RSA-Signer", "RSA-Intermediate", intermediate_key, signer_key.public_key(), False,
                                  eku=[x509.ObjectIdentifier(TIME_STAMPING_OID)], signing_algorithm=signing_algorithm)
        return [der(signer), der(intermediate), der(root)]

    def test_genuine_rsa_pkcs1_sha384_chain_is_accepted(self):
        chain = self.rsa_pkcs1_chain(hashes.SHA384())
        trusted = [chain[-1]]

        self.assertTrue(self.matches(chain, [], trusted), "every link is a genuine RSA PKCS #1 v1.5 SHA-384 signature")

    def test_rsa_pkcs1_sha384_chain_with_tampered_intermediate_signature_is_rejected(self):
        chain = self.rsa_pkcs1_chain(hashes.SHA384())
        tampered_intermediate = bytearray(chain[1])
        tampered_intermediate[-1] ^= 0x01
        chain = [chain[0], bytes(tampered_intermediate), chain[2]]
        trusted = [chain[-1]]

        self.assertFalse(self.matches(chain, [], trusted), "the intermediate's SHA-384 signature no longer verifies under the root's key")

    def test_genuine_rsa_pkcs1_sha512_chain_is_accepted(self):
        chain = self.rsa_pkcs1_chain(hashes.SHA512())
        trusted = [chain[-1]]

        self.assertTrue(self.matches(chain, [], trusted), "every link is a genuine RSA PKCS #1 v1.5 SHA-512 signature")

    def test_rsa_pkcs1_sha512_chain_with_tampered_intermediate_signature_is_rejected(self):
        chain = self.rsa_pkcs1_chain(hashes.SHA512())
        tampered_intermediate = bytearray(chain[1])
        tampered_intermediate[-1] ^= 0x01
        chain = [chain[0], bytes(tampered_intermediate), chain[2]]
        trusted = [chain[-1]]

        self.assertFalse(self.matches(chain, [], trusted), "the intermediate's SHA-512 signature no longer verifies under the root's key")


if __name__ == "__main__":
    unittest.main()
