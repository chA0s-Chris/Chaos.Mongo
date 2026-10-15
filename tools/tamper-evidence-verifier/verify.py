#!/usr/bin/env python3
# Copyright (c) 2025 Christian Flessa. All rights reserved.
# This file is licensed under the MIT license. See LICENSE in the project root for more information.
"""Reference verifier for the tamper evidence of Chaos.Mongo.EventStore, format version 1.

It implements docs/tamper-evidence-specification.md from raw BSON with the Python standard library only, and
delegates the verification of RFC 3161 timestamp tokens and CRL signatures to OpenSSL. It proves that the
specification is sufficient on its own; it is not part of the library.

Usage:
    verify.py vectors DIRECTORY
    verify.py check --events FILE --aggregate-type NAME --aggregate-id UUID
                    [--anchors FILE --leaves FILE --evidence FILE [--evidence-chunks FILE]
                     --trusted-root PEM [--trusted-root PEM ...] [--grace-hours HOURS]]

Input files hold concatenated raw BSON documents, the format mongodump writes. Both commands print a JSON report and
exit with 0 when everything verifies, 1 otherwise and 2 on usage errors.
"""

import argparse
import datetime
import hashlib
import json
import os
import struct
import subprocess
import sys
import tempfile
import uuid

GENESIS_TAG = b"Chaos.Mongo.EventStore/integrity/genesis/v1"
BINDING_TAG = b"Chaos.Mongo.EventStore/integrity/personal-data/v1"
SEAL_MODES = {"Append": 0x01, "Retroactive": 0x02}
ERASURE_DISCRIMINATOR = "Chaos.Mongo.EventStore.PersonalDataErased"
INTEGRITY_ELEMENT = "_integrity"
FORMAT_VERSION = 1
ALGORITHM = "SHA-256"
EVIDENCE_CHUNK_SIZE = 4 * 1024 * 1024

T_DOUBLE, T_STRING, T_DOCUMENT, T_ARRAY, T_BINARY = 0x01, 0x02, 0x03, 0x04, 0x05
T_BOOLEAN, T_DATETIME, T_NULL, T_INT32, T_INT64 = 0x08, 0x09, 0x0A, 0x10, 0x12


def sha256(*parts):
    digest = hashlib.sha256()
    for part in parts:
        digest.update(part)
    return digest.digest()


# --- BSON -----------------------------------------------------------------------------------------------------------


class BsonError(ValueError):
    pass


def _cstring_end(buffer, position):
    end = buffer.find(b"\x00", position)
    if end < 0:
        raise BsonError("unterminated name")
    return end


def _value_size(buffer, bson_type, position):
    fixed = {0x01: 8, 0x06: 0, 0x07: 12, 0x08: 1, 0x09: 8, 0x0A: 0, 0x10: 4, 0x11: 8, 0x12: 8, 0x13: 16, 0x7F: 0, 0xFF: 0}
    if bson_type in fixed:
        return fixed[bson_type]
    if bson_type in (0x02, 0x0D, 0x0E):
        return 4 + struct.unpack_from("<i", buffer, position)[0]
    if bson_type in (0x03, 0x04, 0x0F):
        return struct.unpack_from("<i", buffer, position)[0]
    if bson_type == 0x05:
        return 5 + struct.unpack_from("<i", buffer, position)[0]
    if bson_type == 0x0B:
        first = _cstring_end(buffer, position)
        return _cstring_end(buffer, first + 1) + 1 - position
    if bson_type == 0x0C:
        return 4 + struct.unpack_from("<i", buffer, position)[0] + 12
    raise BsonError(f"unknown BSON type 0x{bson_type:02x}")


def elements(document):
    """Yields (name, type, value bytes, element start, element end) for the top-level elements of a document."""
    length = struct.unpack_from("<i", document, 0)[0]
    if length != len(document) or document[-1] != 0:
        raise BsonError("document length does not match")
    position = 4
    while document[position] != 0:
        bson_type = document[position]
        name_end = _cstring_end(document, position + 1)
        name = document[position + 1:name_end].decode("utf-8")
        value_start = name_end + 1
        value_end = value_start + _value_size(document, bson_type, value_start)
        if value_end > length - 1:
            raise BsonError("element exceeds the document")
        yield name, bson_type, document[value_start:value_end], position, value_end
        position = value_end
    if position != length - 1:
        raise BsonError("trailing bytes in document")


def first(document, name):
    for element_name, bson_type, value, _, _ in elements(document):
        if element_name == name:
            return bson_type, value
    return None


def decode(bson_type, value):
    if bson_type == T_STRING:
        return value[4:-1].decode("utf-8")
    if bson_type in (T_DOCUMENT, T_ARRAY):
        return value
    if bson_type == T_BINARY:
        return value[5:]
    if bson_type == T_INT32:
        return struct.unpack("<i", value)[0]
    if bson_type in (T_INT64, T_DATETIME):
        return struct.unpack("<q", value)[0]
    if bson_type == T_DOUBLE:
        return struct.unpack("<d", value)[0]
    if bson_type == T_BOOLEAN:
        return value != b"\x00"
    if bson_type == T_NULL:
        return None
    return value


def get(document, name, *types):
    found = first(document, name)
    if found is None or (types and found[0] not in types):
        return None
    return decode(*found)


def array_items(array):
    return [(bson_type, value) for _, bson_type, value, _, _ in elements(array)]


def read_documents(path):
    with open(path, "rb") as stream:
        data = stream.read()
    documents, position = [], 0
    while position < len(data):
        length = struct.unpack_from("<i", data, position)[0]
        documents.append(data[position:position + length])
        position += length
    return documents


def guid_bytes(value):
    """The 16 bytes of a UUID in RFC 9562 order, as BSON binary subtype 0x04 stores them."""
    return uuid.UUID(value).bytes


def as_version(bson_type, value):
    """Versions are stored as int64; int32 and integral doubles are accepted, other types are not versions."""
    if bson_type in (T_INT32, T_INT64):
        return decode(bson_type, value)
    if bson_type == T_DOUBLE:
        number = decode(bson_type, value)
        return int(number) if number.is_integer() else None
    return None


def datetime_ms(milliseconds):
    return datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc) + datetime.timedelta(milliseconds=milliseconds)


# --- Canonical form, commitments and the chain rule ------------------------------------------------------------------


def remove_top_level(document, name):
    for element_name, _, _, start, end in elements(document):
        if element_name == name:
            body = document[4:start] + document[end:]
            return struct.pack("<i", len(body) + 4) + body
    return document


def locate(document, path):
    """Locates a dotted path: each segment addresses the first element of that name, every segment but the last an
    embedded document; elements whose names contain a dot are never addressed. Returns (type, value bytes)."""
    current = document
    segments = path.split(".")
    for depth, segment in enumerate(segments):
        found = None
        for element_name, bson_type, value, _, _ in elements(current):
            if "." not in element_name and element_name == segment:
                found = (bson_type, value)
                break
        if found is None:
            return None
        if depth == len(segments) - 1:
            return found
        if found[0] != T_DOCUMENT:
            return None
        current = found[1]
    return None


def replace_with_binary(document, replacements, prefix=""):
    """Replaces the value of every addressed element by binary subtype 0x00 holding the given bytes, keeping its name
    and position, and corrects the length prefixes. Returns the new document and the paths that resolved."""
    body, resolved, seen = bytearray(), set(), set()
    for element_name, bson_type, value, start, end in elements(document):
        raw = document[start:end]
        if "." not in element_name and element_name not in seen:
            seen.add(element_name)
            path = prefix + element_name
            if path in replacements:
                payload = replacements[path]
                raw = bytes([T_BINARY]) + element_name.encode("utf-8") + b"\x00" + struct.pack("<i", len(payload)) + b"\x00" + payload
                resolved.add(path)
            elif bson_type == T_DOCUMENT and any(key.startswith(path + ".") for key in replacements):
                nested, nested_resolved = replace_with_binary(value, replacements, path + ".")
                raw = bytes([T_DOCUMENT]) + element_name.encode("utf-8") + b"\x00" + nested
                resolved |= nested_resolved
        body += raw
    return struct.pack("<i", len(body) + 5) + bytes(body) + b"\x00", resolved


def genesis(aggregate_type, aggregate_id):
    return sha256(GENESIS_TAG, aggregate_type.encode("utf-8"), b"\x00", aggregate_id)


def commitment_of(salt, bson_type, value):
    return sha256(salt, bytes([bson_type]), value)


def binding(paths):
    return BINDING_TAG + b"".join(path.encode("utf-8") + b"\x00" for path in paths)


def chain_hash(canonical, paths, previous_hash, seal_mode):
    return sha256(canonical, binding(paths) if paths is not None else b"", previous_hash, bytes([SEAL_MODES[seal_mode]]))


def read_integrity(value):
    """Reads an _integrity document; returns None when it is malformed."""
    try:
        integrity = {
            "FormatVersion": get(value, "FormatVersion", T_INT32, T_INT64),
            "Algorithm": get(value, "Algorithm", T_STRING),
            "PreviousHash": get(value, "PreviousHash", T_BINARY),
            "Hash": get(value, "Hash", T_BINARY),
            "SealMode": get(value, "SealMode", T_STRING),
            "PersonalData": None,
        }
        if any(integrity[key] is None for key in ("FormatVersion", "Algorithm", "PreviousHash", "Hash", "SealMode")):
            return None
        if integrity["SealMode"] not in SEAL_MODES:
            return None
        personal_data = first(value, "PersonalData")
        if personal_data is not None:
            if personal_data[0] != T_ARRAY:
                return None
            entries = []
            for bson_type, entry in array_items(personal_data[1]):
                if bson_type != T_DOCUMENT:
                    return None
                path = get(entry, "Path", T_STRING)
                commitment = get(entry, "Commitment", T_BINARY)
                salt_element = first(entry, "Salt")
                if path is None or commitment is None or (salt_element is not None and salt_element[0] not in (T_BINARY, T_NULL)):
                    return None
                salt = decode(*salt_element) if salt_element is not None else None
                entries.append({"Path": path, "Salt": salt, "Commitment": commitment})
            integrity["PersonalData"] = entries
        return integrity
    except (BsonError, struct.error, UnicodeDecodeError, IndexError):
        return None


def well_formed(entries):
    paths = set()
    for entry in entries:
        if len(entry["Commitment"]) != 32 or (entry["Salt"] is not None and len(entry["Salt"]) != 32) or not entry["Path"] or entry["Path"] in paths:
            return False
        paths.add(entry["Path"])
    return not any(other.startswith(path + ".") for path in paths for other in paths)


def verify_event(document, previous_hash):
    """Verifies one sealed event. Returns (failure, integrity, [(path, erased)])."""
    found = first(document, INTEGRITY_ELEMENT)
    if found is None:
        return "NotSealed", None, []
    integrity = read_integrity(found[1]) if found[0] == T_DOCUMENT else None
    if integrity is None:
        return "MalformedIntegrity", None, []
    if integrity["FormatVersion"] != FORMAT_VERSION or integrity["Algorithm"] != ALGORITHM:
        return "UnsupportedFormat", None, []
    if integrity["PreviousHash"] != previous_hash:
        return "PreviousHashMismatch", None, []
    stored = remove_top_level(document, INTEGRITY_ELEMENT)
    canonical, statuses, personal_data_failure = stored, [], None
    entries = integrity["PersonalData"]
    if entries is not None:
        if not well_formed(entries):
            return "MalformedIntegrity", None, []
        for entry in entries:
            located = locate(stored, entry["Path"])
            if located is None:
                return "MalformedIntegrity", None, []
            bson_type, value = located
            if entry["Salt"] is not None:
                if commitment_of(entry["Salt"], bson_type, value) != entry["Commitment"]:
                    personal_data_failure = personal_data_failure or "PersonalDataMismatch"
                else:
                    statuses.append((entry["Path"], False))
            elif bson_type == T_NULL:
                statuses.append((entry["Path"], True))
            else:
                personal_data_failure = personal_data_failure or "ErasedPersonalDataPresent"
        canonical, resolved = replace_with_binary(stored, {entry["Path"]: entry["Commitment"] for entry in entries})
        if len(resolved) != len(entries):
            return "MalformedIntegrity", None, []
    paths = [entry["Path"] for entry in entries] if entries is not None else None
    if chain_hash(canonical, paths, previous_hash, integrity["SealMode"]) != integrity["Hash"]:
        return "HashMismatch", None, []
    if personal_data_failure:
        return personal_data_failure, None, []
    return None, integrity, statuses


def is_erasure_event(document):
    found = first(document, "_t")
    if found is None:
        return False
    bson_type, value = found
    if bson_type == T_STRING:
        return decode(bson_type, value) == ERASURE_DISCRIMINATOR
    if bson_type == T_ARRAY:
        items = array_items(value)
        return bool(items) and items[-1][0] == T_STRING and decode(*items[-1]) == ERASURE_DISCRIMINATOR
    return False


def erasure_records(document, erasure_version):
    records = set()
    found = first(document, "Entries")
    if found is None or found[0] != T_ARRAY:
        return records
    for bson_type, entry in array_items(found[1]):
        if bson_type != T_DOCUMENT:
            continue
        version_element = first(entry, "Version")
        paths = first(entry, "Paths")
        version = as_version(*version_element) if version_element else None
        if version is None or version >= erasure_version or paths is None or paths[0] != T_ARRAY:
            continue
        for path_type, path in array_items(paths[1]):
            if path_type == T_STRING:
                records.add((version, decode(path_type, path)))
    return records


def verify_stream(documents, aggregate_id, leaves=None):
    """Verifies the chain of one stream; documents are its events in any order. Leaves are (version, hash) pairs of
    completed anchors, compared with an intact chain."""
    ordered = []
    for document in documents:
        found = first(document, "Version")
        ordered.append((as_version(*found) if found else None, document))
    ordered.sort(key=lambda item: (item[0] is None, item[0] or 0))
    report = {"intact": True, "firstBrokenVersion": None, "failure": None, "events": [], "sealModeTransitions": [], "unrecordedErasures": []}
    previous_hash, recorded, erased = None, set(), []

    def broken(version, failure):
        report.update(intact=False, firstBrokenVersion=version, failure=failure)
        return report

    for expected, (version, document) in enumerate(ordered, start=1):
        if version != expected:
            return broken(expected, "VersionGap")
        if previous_hash is None:
            aggregate_type = get(document, "AggregateType", T_STRING)
            if aggregate_type is None:
                return broken(expected, "MissingAggregateType")
            previous_hash = genesis(aggregate_type, aggregate_id)
        failure, integrity, statuses = verify_event(document, previous_hash)
        if failure:
            return broken(expected, failure)
        report["events"].append({
            "version": expected,
            "hash": integrity["Hash"].hex(),
            "previousHash": integrity["PreviousHash"].hex(),
            "sealMode": integrity["SealMode"],
            "personalData": [{"path": path, "erased": was_erased} for path, was_erased in statuses],
        })
        erased += [(expected, path) for path, was_erased in statuses if was_erased]
        if is_erasure_event(document):
            recorded |= erasure_records(document, expected)
        previous_hash = integrity["Hash"]

    events = report["events"]
    for index in range(1, len(events)):
        if events[index - 1]["sealMode"] == "Append" and events[index]["sealMode"] == "Retroactive":
            report["sealModeTransitions"].append(events[index]["version"])
    for event in events:
        for entry in event["personalData"]:
            entry["status"] = "Present" if not entry["erased"] else ("ErasedWithRecord" if (event["version"], entry["path"]) in recorded else "ErasedWithoutRecord")
            del entry["erased"]
    for leaf_version, leaf_hash in sorted(leaves or []):
        if leaf_version > len(events):
            return broken(len(events) + 1, "AnchoredVersionMissing")
        if bytes.fromhex(events[leaf_version - 1]["hash"]) != leaf_hash:
            return broken(leaf_version, "AnchoredHashMismatch")
    report["unrecordedErasures"] = [[version, path] for version, path in erased if (version, path) not in recorded]
    return report


# --- Merkle tree and anchors ----------------------------------------------------------------------------------------


def encode_leaf(aggregate_type, aggregate_id, version, head_hash):
    name = aggregate_type.encode("utf-8")
    return struct.pack(">i", len(name)) + name + aggregate_id + struct.pack(">q", version) + head_hash


def hash_leaf(data):
    return sha256(b"\x00", data)


def hash_node(left, right):
    return sha256(b"\x01", left, right)


def merkle_levels(leaf_hashes):
    levels = [list(leaf_hashes)]
    while len(levels[-1]) > 1:
        level = levels[-1]
        levels.append([hash_node(level[i], level[i + 1]) if i + 1 < len(level) else level[i] for i in range(0, len(level), 2)])
    return levels


def inclusion_proof(levels, index):
    proof = []
    for level in levels[:-1]:
        if index ^ 1 < len(level):
            proof.append(level[index ^ 1])
        index >>= 1
    return proof


def verify_inclusion(leaf_hash, index, size, proof, root):
    """RFC 9162, section 2.1.3.2."""
    if index < 0 or index >= size:
        return False
    fn, sn, result = index, size - 1, leaf_hash
    for sibling in proof:
        if sn == 0:
            return False
        if fn & 1 == 1 or fn == sn:
            result = hash_node(sibling, result)
            if fn & 1 == 0:
                while True:
                    fn >>= 1
                    sn >>= 1
                    if fn & 1 != 0 or fn == 0:
                        break
        else:
            result = hash_node(result, sibling)
        fn >>= 1
        sn >>= 1
    return sn == 0 and result == root


def anchor_digest(format_version, sequence, previous_digest, merkle_root):
    return sha256(struct.pack(">i", format_version), struct.pack(">q", sequence), previous_digest, merkle_root)


def read_anchor(document):
    try:
        anchor = {
            "Id": get(document, "_id", T_BINARY),
            "Status": get(document, "Status", T_STRING),
            "Sequence": get(document, "Sequence", T_INT32, T_INT64),
            "FormatVersion": get(document, "FormatVersion", T_INT32, T_INT64),
            "HashAlgorithm": get(document, "HashAlgorithm", T_STRING),
            "PreviousDigest": get(document, "PreviousDigest", T_BINARY),
            "MerkleRoot": get(document, "MerkleRoot", T_BINARY),
            "AnchorDigest": get(document, "AnchorDigest", T_BINARY),
            "LeafCount": get(document, "LeafCount", T_INT32, T_INT64),
            "Timestamps": [],
        }
        if any(value is None for value in anchor.values()):
            return None
        timestamps = first(document, "Timestamps")
        for bson_type, entry in array_items(timestamps[1]) if timestamps and timestamps[0] == T_ARRAY else []:
            if bson_type != T_DOCUMENT:
                return None
            anchor["Timestamps"].append({
                "Authority": get(entry, "Authority", T_STRING),
                "TokenHash": get(entry, "TokenHash", T_BINARY),
                "CertificateChainHashes": [decode(*item) for item in array_items(get(entry, "CertificateChainHashes", T_ARRAY) or b"\x05\x00\x00\x00\x00")],
                "ConfirmingRevocationListHashes": [decode(*item) for item in array_items(get(entry, "ConfirmingRevocationListHashes", T_ARRAY) or b"\x05\x00\x00\x00\x00")],
                "TimestampUtc": get(entry, "TimestampUtc", T_DATETIME),
                "RevocationStatus": get(entry, "RevocationStatus", T_STRING),
            })
        return anchor
    except (BsonError, struct.error, UnicodeDecodeError):
        return None


def verify_anchor_chain(anchor_documents):
    anchors = []
    for document in anchor_documents:
        if first(document, "Sequence") is None:
            continue
        anchors.append(document)
    anchors.sort(key=lambda document: get(document, "Sequence", T_INT32, T_INT64) or 0)
    expected, previous = 1, bytes(32)
    for document in anchors:
        anchor = read_anchor(document)
        if anchor is None:
            return {"intact": False, "firstBrokenSequence": expected, "failure": "MalformedAnchor", "anchorCount": expected - 1}
        if anchor["Sequence"] != expected:
            return {"intact": False, "firstBrokenSequence": expected, "failure": "SequenceGap", "anchorCount": expected - 1}
        if anchor["FormatVersion"] != 1 or anchor["HashAlgorithm"] != ALGORITHM or len(anchor["AnchorDigest"]) != 32:
            return {"intact": False, "firstBrokenSequence": expected, "failure": "MalformedAnchor", "anchorCount": expected - 1}
        if anchor["PreviousDigest"] != previous:
            return {"intact": False, "firstBrokenSequence": expected, "failure": "PreviousDigestMismatch", "anchorCount": expected - 1}
        if len(anchor["MerkleRoot"]) != 32 or anchor_digest(1, anchor["Sequence"], anchor["PreviousDigest"], anchor["MerkleRoot"]) != anchor["AnchorDigest"]:
            return {"intact": False, "firstBrokenSequence": expected, "failure": "AnchorDigestMismatch", "anchorCount": expected - 1}
        previous = anchor["AnchorDigest"]
        expected += 1
    return {"intact": True, "firstBrokenSequence": None, "failure": None, "anchorCount": expected - 1}


# --- Evidence -------------------------------------------------------------------------------------------------------


class Evidence:
    """Content-addressed evidence: items by SHA-256, inline or split into chunks of EVIDENCE_CHUNK_SIZE bytes."""

    def __init__(self, evidence_documents, chunk_documents):
        self.items = {get(document, "_id", T_BINARY): document for document in evidence_documents}
        self.chunks = {}
        for document in chunk_documents:
            chunk_id = get(document, "_id", T_DOCUMENT)
            if chunk_id is not None:
                self.chunks[(get(chunk_id, "Hash", T_BINARY), get(chunk_id, "Index", T_INT32, T_INT64))] = get(document, "Data", T_BINARY)

    def resolve(self, item_hash):
        document = self.items.get(item_hash)
        if document is None:
            raise LookupError(f"evidence {item_hash.hex()} is missing")
        length = get(document, "Length", T_INT32, T_INT64)
        data = get(document, "Data", T_BINARY)
        if data is None:
            count = get(document, "ChunkCount", T_INT32, T_INT64) or 0
            parts = [self.chunks.get((item_hash, index)) for index in range(count)]
            if any(part is None for part in parts):
                raise LookupError(f"a chunk of evidence {item_hash.hex()} is missing")
            data = b"".join(parts)
        if len(data) != length or hashlib.sha256(data).digest() != item_hash:
            raise LookupError(f"evidence {item_hash.hex()} does not match its hash")
        return data


def chunked_item(length, multiplier, increment):
    """The deterministic evidence item of the test vectors: byte i is (i * multiplier + increment) mod 256."""
    return bytes((index * multiplier + increment) % 256 for index in range(length))


# --- DER, certificates, CRLs and tokens -----------------------------------------------------------------------------


def der_read(data, position=0):
    """Reads one DER value: returns (tag, content start, content end, value end)."""
    tag = data[position]
    length = data[position + 1]
    start = position + 2
    if length & 0x80:
        count = length & 0x7F
        length = int.from_bytes(data[start:start + count], "big")
        start += count
    return tag, start, start + length, start + length


def der_children(data, start, end):
    children, position = [], start
    while position < end:
        tag, content_start, content_end, value_end = der_read(data, position)
        children.append((tag, content_start, content_end, position, value_end))
        position = value_end
    return children


def der_time(data, tag, start, end):
    text = data[start:end].decode("ascii").rstrip("Z")
    if tag == 0x17:
        year = int(text[:2])
        text = (str(1900 + year) if year >= 50 else str(2000 + year)) + text[2:]
    whole, _, fraction = text.partition(".")
    moment = datetime.datetime.strptime(whole, "%Y%m%d%H%M%S").replace(tzinfo=datetime.timezone.utc)
    return moment + datetime.timedelta(microseconds=int((fraction + "000000")[:6])) if fraction else moment


def certificate_fields(certificate):
    """Returns the encoded issuer and subject, the serial number, and the HTTP CRL distribution points as
    (locations, names a CRL issuer, limited to some reasons)."""
    _, start, end, _ = der_read(certificate)
    tbs = der_children(certificate, start, end)[0]
    fields = der_children(certificate, tbs[1], tbs[2])
    offset = 1 if fields[0][0] == 0xA0 else 0
    serial = certificate[fields[offset][1]:fields[offset][2]]
    issuer = certificate[fields[offset + 2][3]:fields[offset + 2][4]]
    subject = certificate[fields[offset + 4][3]:fields[offset + 4][4]]
    points, names_points = [], False
    for field in fields[offset + 6:]:
        if field[0] != 0xA3:
            continue
        for extension in der_children(certificate, *der_children(certificate, field[1], field[2])[0][1:3]):
            parts = der_children(certificate, extension[1], extension[2])
            if certificate[parts[0][1]:parts[0][2]] != bytes.fromhex("551d1f"):
                continue
            names_points = True
            value = parts[-1]
            for point in der_children(certificate, *der_children(certificate, value[1], value[2])[0][1:3]):
                locations, crl_issuer, reasons = [], False, False
                for part in der_children(certificate, point[1], point[2]):
                    if part[0] == 0xA0:
                        for name in der_children(certificate, part[1], part[2]):
                            if name[0] == 0xA0:
                                for general_name in der_children(certificate, name[1], name[2]):
                                    uri = certificate[general_name[1]:general_name[2]].decode("ascii")
                                    if general_name[0] == 0x86 and uri.lower().startswith(("http://", "https://")):
                                        locations.append(uri)
                    elif part[0] in (0x81, 0xA1):
                        reasons = True
                    elif part[0] == 0xA2:
                        crl_issuer = True
                if locations:
                    points.append((locations, crl_issuer, reasons))
    return {"issuer": issuer, "subject": subject, "serial": serial, "points": points, "namesPoints": names_points}


def crl_sources(chain):
    """The distinct CRLs the distribution points of a chain's non-root certificates name, in capture order:
    (locations, expected issuer or None for an indirect CRL issuer)."""
    sources, keys = [], set()
    for certificate in chain[:-1]:
        fields = certificate_fields(certificate)
        if fields["namesPoints"] and not any(not reasons for _, _, reasons in fields["points"]):
            raise ValueError("a certificate names CRL distribution points, but none with an HTTP location covers all reasons")
        for locations, crl_issuer, _ in fields["points"]:
            issuer = None if crl_issuer else fields["issuer"]
            key = (" ".join(locations), (issuer or b"").hex())
            if key not in keys:
                keys.add(key)
                sources.append((locations, issuer))
    return sources


def crl_fields(crl):
    """Returns the CRL's encoded issuer, its thisUpdate and its entries as (serial, revoked from, issuers, removed)."""
    _, start, end, _ = der_read(crl)
    tbs = der_children(crl, start, end)[0]
    fields = der_children(crl, tbs[1], tbs[2])
    offset = 1 if fields[0][0] == 0x02 else 0
    issuer = crl[fields[offset + 1][3]:fields[offset + 1][4]]
    this_update = der_time(crl, fields[offset + 2][0], fields[offset + 2][1], fields[offset + 2][2])
    entries = []
    for field in fields[offset + 3:]:
        if field[0] != 0x30:
            continue
        children = der_children(crl, field[1], field[2])
        if not children or children[0][0] != 0x30:
            continue
        issuers = [issuer]
        for entry in children:
            parts = der_children(crl, entry[1], entry[2])
            serial = crl[parts[0][1]:parts[0][2]]
            revoked_from = der_time(crl, parts[1][0], parts[1][1], parts[1][2])
            removed = False
            if len(parts) > 2:
                for extension in der_children(crl, parts[2][1], parts[2][2]):
                    extension_parts = der_children(crl, extension[1], extension[2])
                    oid = crl[extension_parts[0][1]:extension_parts[0][2]]
                    value = extension_parts[-1]
                    inner = der_children(crl, value[1], value[2])[0]
                    if oid == bytes.fromhex("551d18"):
                        revoked_from = min(revoked_from, der_time(crl, inner[0], inner[1], inner[2]))
                    elif oid == bytes.fromhex("551d15"):
                        removed = crl[inner[1]:inner[2]] == b"\x08"
                    elif oid == bytes.fromhex("551d1d"):
                        issuers = [crl[name[1]:name[2]] for name in der_children(crl, inner[1], inner[2]) if name[0] == 0xA4]
            entries.append((serial, revoked_from, list(issuers), removed))
        break
    return issuer, this_update, entries


def token_info(token):
    """Extracts the policy OID and genTime from the TSTInfo of an RFC 3161 token (CMS SignedData)."""
    _, start, end, _ = der_read(token)
    content_info = der_children(token, start, end)
    signed_data = der_children(token, *der_children(token, content_info[1][1], content_info[1][2])[0][1:3])
    encapsulated = der_children(token, signed_data[2][1], signed_data[2][2])
    octets = der_children(token, encapsulated[1][1], encapsulated[1][2])[0]
    tst_info = token[octets[1]:octets[2]]
    _, start, end, _ = der_read(tst_info)
    fields = der_children(tst_info, start, end)
    return tst_info[fields[1][3]:fields[1][4]], der_time(tst_info, fields[4][0], fields[4][1], fields[4][2])


def pem(der, label="CERTIFICATE"):
    import base64
    body = base64.encodebytes(der).decode("ascii").replace("\n", "")
    lines = [body[i:i + 64] for i in range(0, len(body), 64)]
    return f"-----BEGIN {label}-----\n" + "\n".join(lines) + f"\n-----END {label}-----\n"


def openssl(*arguments):
    result = subprocess.run(["openssl", *arguments], capture_output=True, text=True)
    return result.returncode, result.stdout + result.stderr


def verify_timestamp(entry, evidence, digest, trusted_roots, grace):
    """Verifies one stored token with OpenSSL and recomputes its revocation status from the confirming CRLs."""
    report = {"authority": entry["Authority"], "valid": False, "failure": None, "revocationStatus": "Unconfirmed", "storedRevocationStatus": entry["RevocationStatus"]}
    try:
        token = evidence.resolve(entry["TokenHash"])
        chain = [evidence.resolve(item) for item in entry["CertificateChainHashes"]]
        confirming = [evidence.resolve(item) for item in entry["ConfirmingRevocationListHashes"]]
    except LookupError as error:
        report["failure"] = str(error)
        return report
    _, token_time = token_info(token)
    if (token_time - datetime_ms(entry["TimestampUtc"])) >= datetime.timedelta(milliseconds=1) or token_time < datetime_ms(entry["TimestampUtc"]):
        report["failure"] = "the token's time differs from the stored time"
        return report
    with tempfile.TemporaryDirectory() as directory:
        token_path, chain_path, roots_path = (os.path.join(directory, name) for name in ("token.der", "chain.pem", "roots.pem"))
        with open(token_path, "wb") as stream:
            stream.write(token)
        with open(chain_path, "w") as stream:
            stream.write("".join(pem(certificate) for certificate in chain))
        with open(roots_path, "w") as stream:
            for root in trusted_roots:
                with open(root) as source:
                    stream.write(source.read())
        code, output = openssl("ts", "-verify", "-digest", digest.hex(), "-in", token_path, "-token_in",
                               "-CAfile", roots_path, "-untrusted", chain_path, "-attime", str(int(token_time.timestamp())))
        if code != 0 or "Verification: OK" not in output:
            report["failure"] = "openssl ts -verify failed: " + output.strip().splitlines()[-1] if output.strip() else "openssl ts -verify failed"
            return report
        report["revocationStatus"] = revocation_status(chain, confirming, token_time, grace, directory)
    if report["revocationStatus"] == "Revoked":
        report["failure"] = "a certificate of the token's chain is revoked at or before the token's time"
        return report
    report["valid"] = True
    return report


def revocation_status(chain, confirming, token_time, grace, directory):
    try:
        sources = crl_sources(chain)
    except (ValueError, IndexError):
        return "Unconfirmed"
    confirmed = len(sources) == len(confirming)
    entries = []
    for (_, expected_issuer), crl in zip(sources, confirming):
        try:
            issuer, this_update, crl_entries = crl_fields(crl)
        except (ValueError, IndexError):
            confirmed = False
            continue
        issuer_certificate = next((certificate for certificate in chain if certificate_fields(certificate)["subject"] == expected_issuer), None)
        if expected_issuer is None or issuer != expected_issuer or this_update < token_time + grace or issuer_certificate is None:
            confirmed = False
            continue
        crl_path, issuer_path = os.path.join(directory, "crl.der"), os.path.join(directory, "issuer.pem")
        with open(crl_path, "wb") as stream:
            stream.write(crl)
        with open(issuer_path, "w") as stream:
            stream.write(pem(issuer_certificate))
        code, output = openssl("crl", "-inform", "DER", "-in", crl_path, "-CAfile", issuer_path, "-noout")
        if code != 0 or "verify OK" not in output:
            confirmed = False
            continue
        entries += crl_entries
    for certificate in chain[:-1]:
        fields = certificate_fields(certificate)
        for serial, revoked_from, issuers, removed in entries:
            if not removed and serial == fields["serial"] and (not issuers or fields["issuer"] in issuers) and revoked_from <= token_time:
                return "Revoked"
    return "Confirmed" if confirmed else "Unconfirmed"


def verify_anchor(anchor, evidence, trusted_roots, grace):
    digest_valid = anchor_digest(anchor["FormatVersion"], anchor["Sequence"], anchor["PreviousDigest"], anchor["MerkleRoot"]) == anchor["AnchorDigest"]
    timestamps = [verify_timestamp(entry, evidence, anchor["AnchorDigest"], trusted_roots, grace) for entry in anchor["Timestamps"]]
    return {"sequence": anchor["Sequence"], "digestValid": digest_valid, "timestamps": timestamps,
            "verified": digest_valid and any(timestamp["valid"] for timestamp in timestamps)}


# --- Commands -------------------------------------------------------------------------------------------------------


def stream_documents(documents, aggregate_id):
    return [document for document in documents if get(document, "AggregateId", T_BINARY) == aggregate_id]


def check(arguments):
    aggregate_id = guid_bytes(arguments.aggregate_id)
    events = stream_documents(read_documents(arguments.events), aggregate_id)
    report = {"stream": None, "anchorChain": None, "anchors": []}
    if not arguments.anchors:
        report["stream"] = verify_stream(events, aggregate_id)
        return report, report["stream"]["intact"]

    anchor_documents = read_documents(arguments.anchors)
    anchors = {anchor["Id"]: anchor for anchor in (read_anchor(document) for document in anchor_documents) if anchor}
    evidence = Evidence(read_documents(arguments.evidence), read_documents(arguments.evidence_chunks) if arguments.evidence_chunks else [])
    grace = datetime.timedelta(hours=arguments.grace_hours)
    leaves = []
    for document in read_documents(arguments.leaves):
        anchor = anchors.get(get(document, "AnchorId", T_BINARY))
        if (get(document, "AggregateType", T_STRING) == arguments.aggregate_type and get(document, "AggregateId", T_BINARY) == aggregate_id and
                anchor is not None and anchor["Status"] == "Completed" and anchor["Sequence"] == get(document, "AnchorSequence", T_INT32, T_INT64)):
            leaves.append((anchor, document))
    report["anchorChain"] = verify_anchor_chain(anchor_documents)
    report["stream"] = verify_stream(events, aggregate_id, [(get(leaf, "Version", T_INT32, T_INT64), get(leaf, "Hash", T_BINARY)) for _, leaf in leaves])
    ok = report["stream"]["intact"] and report["anchorChain"]["intact"]
    for anchor, leaf in leaves:
        leaf_hash = hash_leaf(encode_leaf(arguments.aggregate_type, aggregate_id, get(leaf, "Version", T_INT32, T_INT64), get(leaf, "Hash", T_BINARY)))
        proof = [decode(*item) for item in array_items(get(leaf, "Proof", T_ARRAY))]
        anchor_report = verify_anchor(anchor, evidence, arguments.trusted_root, grace)
        anchor_report["inclusionValid"] = verify_inclusion(leaf_hash, get(leaf, "Index", T_INT32, T_INT64), anchor["LeafCount"], proof, anchor["MerkleRoot"])
        anchor_report["leafVersion"] = get(leaf, "Version", T_INT32, T_INT64)
        ok = ok and anchor_report["verified"] and anchor_report["inclusionValid"]
        report["anchors"].append(anchor_report)
    return report, ok


def vectors(directory):
    """Checks every test vector against its expected values."""
    failures = []

    def expect(name, actual, expected):
        if actual != expected:
            failures.append({"vector": name, "expected": expected, "actual": actual})

    with open(os.path.join(directory, "streams.json")) as stream:
        expected_streams = json.load(stream)
    documents = read_documents(os.path.join(directory, "streams.bson"))
    for expected in expected_streams["streams"]:
        aggregate_id = guid_bytes(expected["aggregateId"])
        events = stream_documents(documents, aggregate_id)
        if events:
            expect(f"genesis {expected['name']}", genesis(get(events[0], "AggregateType", T_STRING) or "", aggregate_id).hex(), expected["genesis"])
        expect(f"stream {expected['name']}", verify_stream(events, aggregate_id), expected["result"])

    with open(os.path.join(directory, "merkle.json")) as stream:
        for tree in json.load(stream)["trees"]:
            leaf_hashes = [hash_leaf(bytes.fromhex(leaf)) for leaf in tree["leaves"]]
            levels = merkle_levels(leaf_hashes)
            expect(f"merkle root {len(leaf_hashes)}", levels[-1][0].hex(), tree["root"])
            for index, proof in enumerate(tree["proofs"]):
                expect(f"merkle proof {len(leaf_hashes)}/{index}", [item.hex() for item in inclusion_proof(levels, index)], proof)
                expect(f"merkle inclusion {len(leaf_hashes)}/{index}",
                       verify_inclusion(leaf_hashes[index], index, len(leaf_hashes), [bytes.fromhex(item) for item in proof], levels[-1][0]), True)
            for leaf in tree.get("candidates", []):
                expect("merkle leaf encoding",
                       encode_leaf(leaf["aggregateType"], guid_bytes(leaf["aggregateId"]), leaf["version"], bytes.fromhex(leaf["hash"])).hex(),
                       leaf["encoded"])

    with open(os.path.join(directory, "anchors.json")) as stream:
        expected_anchors = json.load(stream)
    anchor_documents = read_documents(os.path.join(directory, "anchors.bson"))
    expect("anchor chain", verify_anchor_chain(anchor_documents), expected_anchors["chain"])
    evidence = Evidence(read_documents(os.path.join(directory, "evidence.bson")), [])
    roots = [os.path.join(directory, name) for name in expected_anchors["trustedRoots"]]
    grace = datetime.timedelta(hours=expected_anchors["graceHours"])
    for document, expected in zip(sorted(anchor_documents, key=lambda d: get(d, "Sequence", T_INT32, T_INT64)), expected_anchors["anchors"]):
        anchor = read_anchor(document)
        expect(f"anchor digest {anchor['Sequence']}", anchor_digest(1, anchor["Sequence"], anchor["PreviousDigest"], anchor["MerkleRoot"]).hex(), expected["digest"])
        result = verify_anchor(anchor, evidence, roots, grace)
        expect(f"anchor {anchor['Sequence']}", [(t["valid"], t["revocationStatus"]) for t in result["timestamps"]],
               [tuple(item) for item in expected["timestamps"]])
    leaf_documents = read_documents(os.path.join(directory, "leaves.bson"))
    anchors = {anchor["Id"]: anchor for anchor in (read_anchor(document) for document in anchor_documents)}
    for leaf in leaf_documents:
        anchor = anchors[get(leaf, "AnchorId", T_BINARY)]
        leaf_hash = hash_leaf(encode_leaf(get(leaf, "AggregateType", T_STRING), get(leaf, "AggregateId", T_BINARY), get(leaf, "Version", T_INT32, T_INT64), get(leaf, "Hash", T_BINARY)))
        proof = [decode(*item) for item in array_items(get(leaf, "Proof", T_ARRAY))]
        expect("anchored leaf inclusion", verify_inclusion(leaf_hash, get(leaf, "Index", T_INT32, T_INT64), anchor["LeafCount"], proof, anchor["MerkleRoot"]), True)

    with open(os.path.join(directory, "evidence-chunked.json")) as stream:
        chunked = json.load(stream)
    item = chunked_item(chunked["length"], chunked["multiplier"], chunked["increment"])
    chunks = [item[offset:offset + EVIDENCE_CHUNK_SIZE] for offset in range(0, len(item), EVIDENCE_CHUNK_SIZE)]
    expect("chunked item hash", hashlib.sha256(item).hexdigest(), chunked["hash"])
    expect("chunk hashes", [hashlib.sha256(chunk).hexdigest() for chunk in chunks], chunked["chunkHashes"])
    resolved = Evidence([_chunked_evidence_document(item, len(chunks))], [_chunk_document(hashlib.sha256(item).digest(), index, chunk) for index, chunk in enumerate(chunks)])
    expect("chunked item reassembly", resolved.resolve(hashlib.sha256(item).digest()) == item, True)
    return {"failures": failures}, not failures


def _encode_document(fields):
    body = b""
    for name, bson_type, value in fields:
        body += bytes([bson_type]) + name.encode("utf-8") + b"\x00" + value
    return struct.pack("<i", len(body) + 5) + body + b"\x00"


def _binary(data):
    return struct.pack("<i", len(data)) + b"\x00" + data


def _chunked_evidence_document(item, count):
    digest = hashlib.sha256(item).digest()
    return _encode_document([("_id", T_BINARY, _binary(digest)), ("Length", T_INT32, struct.pack("<i", len(item))), ("ChunkCount", T_INT32, struct.pack("<i", count))])


def _chunk_document(digest, index, chunk):
    chunk_id = _encode_document([("Hash", T_BINARY, _binary(digest)), ("Index", T_INT32, struct.pack("<i", index))])
    return _encode_document([("_id", T_DOCUMENT, chunk_id), ("Data", T_BINARY, _binary(chunk))])


def main():
    parser = argparse.ArgumentParser(description="Reference verifier for Chaos.Mongo.EventStore tamper evidence, format version 1.")
    commands = parser.add_subparsers(dest="command", required=True)
    vectors_parser = commands.add_parser("vectors", help="check the test vectors in a directory")
    vectors_parser.add_argument("directory")
    check_parser = commands.add_parser("check", help="verify a stream and its anchors from mongodump files")
    check_parser.add_argument("--events", required=True)
    check_parser.add_argument("--aggregate-type", required=True)
    check_parser.add_argument("--aggregate-id", required=True)
    check_parser.add_argument("--anchors")
    check_parser.add_argument("--leaves")
    check_parser.add_argument("--evidence")
    check_parser.add_argument("--evidence-chunks")
    check_parser.add_argument("--trusted-root", action="append", default=[])
    check_parser.add_argument("--grace-hours", type=float, default=24)
    arguments = parser.parse_args()
    if arguments.command == "vectors":
        report, ok = vectors(arguments.directory)
    else:
        if arguments.anchors and not (arguments.leaves and arguments.evidence and arguments.trusted_root):
            parser.error("--anchors needs --leaves, --evidence and at least one --trusted-root")
        report, ok = check(arguments)
    json.dump(report, sys.stdout, indent=2, default=lambda value: value.hex() if isinstance(value, bytes) else str(value))
    sys.stdout.write("\n")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
