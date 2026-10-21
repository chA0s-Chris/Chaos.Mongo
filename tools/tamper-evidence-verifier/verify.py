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
                     --trusted-root [NAME=]PEM [--trusted-root [NAME=]PEM ...] [--policy NAME=OID ...]
                     [--grace-hours HOURS] [--trusted-head SEQUENCE:DIGESTHEX]]

A trusted root without a name applies to every timestamp authority, one with a name only to that authority's tokens.
Input files hold concatenated raw BSON documents, the format mongodump writes. Both commands print a JSON report and
exit with 0 when everything verifies, 1 otherwise and 2 on usage errors or unreadable input files.
"""

import argparse
import base64
import datetime
import hashlib
import json
import os
import re
import struct
import subprocess
import sys
import tempfile
import urllib.parse
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
SUBTYPE_GENERIC, SUBTYPE_UUID = 0x00, 0x04

# Errors that malformed BSON or DER input raises while it is parsed.
PARSE_ERRORS = (ValueError, struct.error, UnicodeDecodeError, IndexError, KeyError, TypeError)


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
        first_end = _cstring_end(buffer, position)
        return _cstring_end(buffer, first_end + 1) + 1 - position
    if bson_type == 0x0C:
        return 4 + struct.unpack_from("<i", buffer, position)[0] + 12
    raise BsonError(f"unknown BSON type 0x{bson_type:02x}")


def elements(document):
    """Yields (name, type, value bytes, element start, element end) for the top-level elements of a document."""
    length = struct.unpack_from("<i", document, 0)[0]
    if length != len(document) or length < 5 or document[-1] != 0:
        raise BsonError("document length does not match")
    position = 4
    while document[position] != 0:
        bson_type = document[position]
        name_end = _cstring_end(document, position + 1)
        name = document[position + 1:name_end].decode("utf-8")
        value_start = name_end + 1
        value_size = _value_size(document, bson_type, value_start)
        value_end = value_start + value_size
        if value_size < 0 or value_end > length - 1:
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


def get_binary(document, name, subtype):
    """Returns the bytes of a binary element of the given subtype, or None for another type or subtype."""
    found = first(document, name)
    if found is None or found[0] != T_BINARY or found[1][4] != subtype:
        return None
    return found[1][5:]


def array_items(array):
    return [(bson_type, value) for _, bson_type, value, _, _ in elements(array)]


def read_documents(path):
    """Reads concatenated documents. A length prefix below 5 or beyond the end of the file makes the file malformed."""
    with open(path, "rb") as stream:
        data = stream.read()
    documents, position = [], 0
    while position < len(data):
        if len(data) - position < 4:
            raise BsonError(f"malformed BSON file {path}: truncated length prefix at offset {position}")
        length = struct.unpack_from("<i", data, position)[0]
        if length < 5 or length > len(data) - position:
            raise BsonError(f"malformed BSON file {path}: document length {length} at offset {position}")
        documents.append(data[position:position + length])
        position += length
    return documents


def guid_bytes(value):
    """The 16 bytes of a UUID in RFC 9562 order, as BSON binary subtype 0x04 stores them."""
    return uuid.UUID(value).bytes


def as_version(bson_type, value):
    """Versions are stored as int64; int32 and integral doubles within the int64 range are accepted, other types,
    including decimal128, are not versions."""
    if bson_type in (T_INT32, T_INT64):
        return decode(bson_type, value)
    if bson_type == T_DOUBLE:
        number = decode(bson_type, value)
        return int(number) if number.is_integer() and -2.0 ** 63 <= number < 2.0 ** 63 else None
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
    """Reads an _integrity document; returns None when it is malformed. Every element must have exactly the type the
    format writes: FormatVersion int32, Algorithm and SealMode strings, the hashes, commitments and salts binary
    subtype 0x00, PersonalData an array of documents with a string Path. An erased salt is absent, not null."""
    try:
        integrity = {
            "FormatVersion": get(value, "FormatVersion", T_INT32),
            "Algorithm": get(value, "Algorithm", T_STRING),
            "PreviousHash": get_binary(value, "PreviousHash", SUBTYPE_GENERIC),
            "Hash": get_binary(value, "Hash", SUBTYPE_GENERIC),
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
                commitment = get_binary(entry, "Commitment", SUBTYPE_GENERIC)
                salt = get_binary(entry, "Salt", SUBTYPE_GENERIC)
                if path is None or commitment is None or (salt is None and first(entry, "Salt") is not None):
                    return None
                entries.append({"Path": path, "Salt": salt, "Commitment": commitment})
            integrity["PersonalData"] = entries
        return integrity
    except PARSE_ERRORS:
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


def read_version(document):
    """The version of an event document, or None when it is missing, not a version or the document is malformed."""
    try:
        found = first(document, "Version")
        return as_version(*found) if found else None
    except PARSE_ERRORS:
        return None


def verify_stream(documents, aggregate_id, leaves=None):
    """Verifies the chain of one stream; documents are its events in any order. Leaves are (version, hash, proves
    inclusion) triples of completed anchors in anchor sequence order, compared with an intact chain."""
    ordered = [(read_version(document), document) for document in documents]
    ordered.sort(key=lambda item: (item[0] is None, item[0] or 0))
    report = {"intact": True, "firstBrokenVersion": None, "failure": None, "events": [], "sealModeTransitions": [], "unrecordedErasures": []}
    previous_hash, recorded, erased = None, set(), []

    def broken(version, failure):
        report.update(intact=False, firstBrokenVersion=version, failure=failure)
        return report

    for expected, (version, document) in enumerate(ordered, start=1):
        if version != expected:
            return broken(expected, "VersionGap")
        try:
            if previous_hash is None:
                aggregate_type = get(document, "AggregateType", T_STRING)
                if aggregate_type is None:
                    return broken(expected, "MissingAggregateType")
                previous_hash = genesis(aggregate_type, aggregate_id)
            failure, integrity, statuses = verify_event(document, previous_hash)
            if not failure and is_erasure_event(document):
                recorded |= erasure_records(document, expected)
        except PARSE_ERRORS:
            failure = "MalformedIntegrity"
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
        previous_hash = integrity["Hash"]

    events = report["events"]
    for index in range(1, len(events)):
        if events[index - 1]["sealMode"] == "Append" and events[index]["sealMode"] == "Retroactive":
            report["sealModeTransitions"].append(events[index]["version"])
    for event in events:
        for entry in event["personalData"]:
            entry["status"] = "Present" if not entry["erased"] else ("ErasedWithRecord" if (event["version"], entry["path"]) in recorded else "ErasedWithoutRecord")
            del entry["erased"]
    lowest = None
    for leaf_version, leaf_hash, proves_inclusion in leaves or []:
        if not proves_inclusion:
            finding = (leaf_version, "AnchoredLeafInvalid")
        elif leaf_version > len(events):
            finding = (len(events) + 1, "AnchoredVersionMissing")
        elif bytes.fromhex(events[leaf_version - 1]["hash"]) != leaf_hash:
            finding = (leaf_version, "AnchoredHashMismatch")
        else:
            continue
        if lowest is None or finding[0] < lowest[0]:
            lowest = finding
    if lowest is not None:
        return broken(*lowest)
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


def binary_list(entry, name):
    """The items of an array of binaries; None when it is missing or holds anything else."""
    items = get(entry, name, T_ARRAY)
    if items is None:
        return None
    values = array_items(items)
    return [decode(*item) for item in values] if all(bson_type == T_BINARY for bson_type, _ in values) else None


def read_anchor(document):
    """Reads an anchor document; returns None when it is malformed. Its identifier must be a standard UUID."""
    try:
        anchor = {
            "Id": get_binary(document, "_id", SUBTYPE_UUID),
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
            timestamp = {
                "Authority": get(entry, "Authority", T_STRING),
                "TokenHash": get(entry, "TokenHash", T_BINARY),
                "CertificateChainHashes": binary_list(entry, "CertificateChainHashes"),
                "CertificateRevocationListHashes": binary_list(entry, "CertificateRevocationListHashes"),
                "ConfirmingRevocationListHashes": binary_list(entry, "ConfirmingRevocationListHashes") if first(entry, "ConfirmingRevocationListHashes") else [],
                "TimestampUtc": get(entry, "TimestampUtc", T_DATETIME),
                "RevocationStatus": get(entry, "RevocationStatus", T_STRING),
            }
            if any(value is None for value in timestamp.values()):
                return None
            anchor["Timestamps"].append(timestamp)
        return anchor
    except PARSE_ERRORS:
        return None


def read_leaf(document):
    """Reads a leaf document; returns None when it is malformed. Identifiers must be standard UUIDs and the version a
    whole number of at least 1."""
    try:
        version_element = first(document, "Version")
        version = as_version(*version_element) if version_element else None
        proof = binary_list(document, "Proof")
        leaf = {
            "AnchorId": get_binary(document, "AnchorId", SUBTYPE_UUID),
            "AnchorSequence": get(document, "AnchorSequence", T_INT32, T_INT64),
            "Index": get(document, "Index", T_INT32, T_INT64),
            "AggregateType": get(document, "AggregateType", T_STRING),
            "AggregateId": get_binary(document, "AggregateId", SUBTYPE_UUID),
            "Version": version if version is not None and version >= 1 else None,
            "Hash": get(document, "Hash", T_BINARY),
            "Proof": proof,
        }
        return None if any(value is None for value in leaf.values()) else leaf
    except PARSE_ERRORS:
        return None


def leaf_hash_of(leaf):
    return hash_leaf(encode_leaf(leaf["AggregateType"], leaf["AggregateId"], leaf["Version"], leaf["Hash"]))


def proves_inclusion(leaf, anchor):
    return len(leaf["Hash"]) == 32 and verify_inclusion(leaf_hash_of(leaf), leaf["Index"], anchor["LeafCount"], leaf["Proof"], anchor["MerkleRoot"])


def leaf_documents_by_anchor(leaf_documents):
    """Groups leaf documents by the standard-UUID anchor identifier they name; others belong to no anchor."""
    groups = {}
    for document in leaf_documents:
        try:
            anchor_id = get_binary(document, "AnchorId", SUBTYPE_UUID)
        except PARSE_ERRORS:
            anchor_id = None
        if anchor_id is not None:
            groups.setdefault(anchor_id, []).append(document)
    return groups


def recomputes_root(anchor, documents):
    """Recomputes an anchor's Merkle root from its leaves: exactly LeafCount readable leaves with the indexes from 0 that
    carry the anchor's sequence number."""
    leaves = [read_leaf(document) for document in documents]
    if anchor["LeafCount"] < 1 or len(leaves) != anchor["LeafCount"] or any(leaf is None for leaf in leaves):
        return False
    leaves.sort(key=lambda leaf: leaf["Index"])
    if any(leaf["Index"] != index or leaf["AnchorSequence"] != anchor["Sequence"] or len(leaf["Hash"]) != 32 for index, leaf in enumerate(leaves)):
        return False
    return merkle_levels([leaf_hash_of(leaf) for leaf in leaves])[-1][0] == anchor["MerkleRoot"]


def verify_anchor_chain(anchor_documents, leaf_documents, trusted_head=None):
    """Verifies the committed anchors in sequence order: contiguous sequence numbers, a supported format, previous
    digests, recomputed digests, roots recomputed from the stored leaves and, for every anchor but the newest, the
    completed status. A trusted head (sequence, digest) must be contained unchanged."""
    anchors = []
    for document in anchor_documents:
        try:
            if first(document, "Sequence") is None:
                continue
            sequence = get(document, "Sequence", T_INT32, T_INT64)
        except PARSE_ERRORS:
            sequence = None
        anchors.append((sequence if sequence is not None else 0, document))
    anchors.sort(key=lambda item: item[0])
    leaves = leaf_documents_by_anchor(leaf_documents)
    expected, previous, previous_awaits, head_digest = 1, bytes(32), False, None

    def broken(sequence, failure, count):
        return {"intact": False, "firstBrokenSequence": sequence, "failure": failure, "anchorCount": count}

    for _, document in anchors:
        anchor = read_anchor(document)
        if anchor is None:
            return broken(expected, "MalformedAnchor", expected - 1)
        if anchor["Sequence"] != expected:
            return broken(expected, "SequenceGap", expected - 1)
        if previous_awaits:
            return broken(expected - 1, "MalformedAnchor", expected - 2)
        if (anchor["Status"] not in ("AwaitingTimestamp", "Completed") or anchor["FormatVersion"] != 1 or anchor["HashAlgorithm"] != ALGORITHM or
                len(anchor["AnchorDigest"]) != 32):
            return broken(expected, "MalformedAnchor", expected - 1)
        if anchor["PreviousDigest"] != previous:
            return broken(expected, "PreviousDigestMismatch", expected - 1)
        if (len(anchor["PreviousDigest"]) != 32 or len(anchor["MerkleRoot"]) != 32 or
                anchor_digest(1, anchor["Sequence"], anchor["PreviousDigest"], anchor["MerkleRoot"]) != anchor["AnchorDigest"]):
            return broken(expected, "AnchorDigestMismatch", expected - 1)
        if not recomputes_root(anchor, leaves.get(anchor["Id"], [])):
            return broken(expected, "LeafSetMismatch", expected - 1)
        if trusted_head is not None and anchor["Sequence"] == trusted_head[0]:
            head_digest = anchor["AnchorDigest"]
        previous, previous_awaits = anchor["AnchorDigest"], anchor["Status"] == "AwaitingTimestamp"
        expected += 1
    if trusted_head is not None:
        if head_digest is None:
            return broken(expected, "TrustedHeadMissing", expected - 1)
        if head_digest != trusted_head[1]:
            return broken(trusted_head[0], "TrustedHeadMismatch", expected - 1)
    return {"intact": True, "firstBrokenSequence": None, "failure": None, "anchorCount": expected - 1}


# --- Evidence -------------------------------------------------------------------------------------------------------


class Evidence:
    """Content-addressed evidence: items by SHA-256, inline or split into chunks of EVIDENCE_CHUNK_SIZE bytes. Documents
    that cannot be read are skipped and listed in malformed."""

    def __init__(self, evidence_documents, chunk_documents):
        self.items, self.chunks, self.malformed = {}, {}, []
        for index, document in enumerate(evidence_documents):
            try:
                self.items[get(document, "_id", T_BINARY)] = document
            except PARSE_ERRORS as error:
                self.malformed.append({"collection": "evidence", "index": index, "error": str(error)})
        for index, document in enumerate(chunk_documents):
            try:
                chunk_id = get(document, "_id", T_DOCUMENT)
                if chunk_id is not None:
                    self.chunks[(get(chunk_id, "Hash", T_BINARY), get(chunk_id, "Index", T_INT32, T_INT64))] = get(document, "Data", T_BINARY)
            except PARSE_ERRORS as error:
                self.malformed.append({"collection": "evidenceChunks", "index": index, "error": str(error)})

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


def chunked_item(length, multiplier, increment, modulus):
    """The deterministic evidence item of the test vectors: byte i is (i * multiplier + increment) mod modulus."""
    return bytes((index * multiplier + increment) % modulus for index in range(length))


# --- DER, certificates, CRLs and tokens -----------------------------------------------------------------------------


OID_ID_CT_TSTINFO = "1.2.840.113549.1.9.16.1.4"
OID_COUNTERSIGNATURE = "1.2.840.113549.1.9.6"
OID_DELTA_CRL_INDICATOR = "2.5.29.27"
OID_ISSUING_DISTRIBUTION_POINT = "2.5.29.28"
OID_CRL_NUMBER = "2.5.29.20"
OID_RSASSA_PSS = "1.2.840.113549.1.1.10"
OID_MGF1 = "1.2.840.113549.1.1.8"
SHA2_DIGESTS = {"2.16.840.1.101.3.4.2.1": 32, "2.16.840.1.101.3.4.2.2": 48, "2.16.840.1.101.3.4.2.3": 64}
# MD2, MD4, MD5 and SHA-1, which are not collision resistant, and the signature algorithms that name them.
WEAK_DIGESTS = {"1.2.840.113549.2.2", "1.2.840.113549.2.4", "1.2.840.113549.2.5", "1.3.14.3.2.26"}
WEAK_SIGNATURES = {"1.2.840.113549.1.1.2", "1.2.840.113549.1.1.3", "1.2.840.113549.1.1.4", "1.2.840.113549.1.1.5", "1.3.14.3.2.29",
                   "1.2.840.10045.4.1", "1.2.840.10040.4.3"}
# The CRL signature algorithms that are accepted besides RSASSA-PSS: RSA PKCS #1 v1.5 and ECDSA with SHA-256, -384 and -512.
CRL_SIGNATURES = {"1.2.840.113549.1.1.11", "1.2.840.113549.1.1.12", "1.2.840.113549.1.1.13", "1.2.840.10045.4.3.2", "1.2.840.10045.4.3.3",
                  "1.2.840.10045.4.3.4"}


def der_read(data, position=0):
    """Reads one DER value: returns (tag, content start, content end, value end)."""
    tag = data[position]
    length = data[position + 1]
    start = position + 2
    if length & 0x80:
        count = length & 0x7F
        if count == 0 or count > 4:
            raise ValueError("unsupported DER length")
        length = int.from_bytes(data[start:start + count], "big")
        start += count
    if start + length > len(data):
        raise ValueError("DER value exceeds its container")
    return tag, start, start + length, start + length


def der_children(data, start, end):
    children, position = [], start
    while position < end:
        tag, content_start, content_end, value_end = der_read(data, position)
        if value_end > end:
            raise ValueError("DER value exceeds its container")
        children.append((tag, content_start, content_end, position, value_end))
        position = value_end
    return children


def der_single(data):
    """Requires the data to be exactly one DER value and returns (tag, content start, content end)."""
    tag, start, end, value_end = der_read(data)
    if value_end != len(data):
        raise ValueError("data follows the DER value")
    return tag, start, end


def decode_oid(content):
    """Decodes the content bytes of an OBJECT IDENTIFIER to its dotted form."""
    numbers, value = [], 0
    for byte in content:
        value = (value << 7) | (byte & 0x7F)
        if not byte & 0x80:
            numbers.append(value)
            value = 0
    if not numbers:
        raise ValueError("empty object identifier")
    head = numbers[0]
    prefix = [0, head] if head < 40 else [1, head - 40] if head < 80 else [2, head - 80]
    return ".".join(str(number) for number in prefix + numbers[1:])


def der_oid(data, child):
    if child[0] != 0x06:
        raise ValueError("expected an object identifier")
    return decode_oid(data[child[1]:child[2]])


def der_time(data, tag, start, end):
    text = data[start:end].decode("ascii").rstrip("Z")
    if tag == 0x17:
        year = int(text[:2])
        text = (str(1900 + year) if year >= 50 else str(2000 + year)) + text[2:]
    whole, _, fraction = text.partition(".")
    moment = datetime.datetime.strptime(whole, "%Y%m%d%H%M%S").replace(tzinfo=datetime.timezone.utc)
    return moment + datetime.timedelta(microseconds=int((fraction + "000000")[:6])) if fraction else moment


def normalize_uri(text):
    """Normalizes an HTTP or HTTPS URI as .NET's Uri.AbsoluteUri does for the names that matter here: lower-case scheme
    and host, no default port, '/' for an empty path, dot segments removed. Returns None for other URIs."""
    try:
        parts = urllib.parse.urlsplit(text)
        scheme, host, port = parts.scheme.lower(), (parts.hostname or "").lower(), parts.port
    except ValueError:
        return None
    if scheme not in ("http", "https") or not host:
        return None
    netloc = (parts.netloc.rpartition("@")[0] + "@" if "@" in parts.netloc else "") + host
    if port is not None and port != {"http": 80, "https": 443}[scheme]:
        netloc += f":{port}"
    segments = []
    for segment in (parts.path or "/").split("/")[1:]:
        if segment == "..":
            if segments:
                segments.pop()
        elif segment != ".":
            segments.append(segment)
    path = "/" + "/".join(segments)
    return urllib.parse.urlunsplit((scheme, netloc, path, parts.query, parts.fragment))


def covers_all_reasons(bits):
    """ReasonFlags name every revocation reason when bits 1 to 8 are set; bit 0 is unused (RFC 5280, 4.2.1.13). The
    first content byte of the BIT STRING counts its unused bits."""
    flags = bits[1:]
    return len(flags) >= 2 and flags[0] & 0x7F == 0x7F and flags[1] & 0x80 == 0x80


def certificate_fields(certificate):
    """Returns the encoded issuer and subject, the serial number, and the HTTP CRL distribution points as
    (normalized locations, names a CRL issuer, covers all reasons)."""
    _, start, end = der_single(certificate)
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
            if der_oid(certificate, parts[0]) != "2.5.29.31":
                continue
            names_points = True
            value = parts[-1]
            for point in der_children(certificate, *der_children(certificate, value[1], value[2])[0][1:3]):
                locations, crl_issuer, all_reasons = [], False, True
                for part in der_children(certificate, point[1], point[2]):
                    if part[0] == 0xA0:
                        for name in der_children(certificate, part[1], part[2]):
                            if name[0] == 0xA0:
                                for general_name in der_children(certificate, name[1], name[2]):
                                    if general_name[0] == 0x86:
                                        uri = normalize_uri(certificate[general_name[1]:general_name[2]].decode("ascii"))
                                        if uri is not None:
                                            locations.append(uri)
                    elif part[0] == 0x81:
                        all_reasons = covers_all_reasons(certificate[part[1]:part[2]])
                    elif part[0] == 0xA2:
                        crl_issuer = True
                if locations:
                    points.append((locations, crl_issuer, all_reasons))
    return {"issuer": issuer, "subject": subject, "serial": serial, "points": points, "namesPoints": names_points}


def crl_sources(chain):
    """The distinct CRLs the distribution points of a chain's non-root certificates name, in capture order:
    (locations, expected issuer or None for an indirect CRL issuer)."""
    sources, keys = [], set()
    for certificate in chain[:-1]:
        fields = certificate_fields(certificate)
        if fields["namesPoints"] and not any(all_reasons for _, _, all_reasons in fields["points"]):
            raise ValueError("a certificate names CRL distribution points, but none with an HTTP location covers all reasons")
        for locations, crl_issuer, _ in fields["points"]:
            issuer = None if crl_issuer else fields["issuer"]
            key = (" ".join(locations), (issuer or b"").hex())
            if key not in keys:
                keys.add(key)
                sources.append((locations, issuer))
    return sources


def crl_parts(crl):
    """Returns the CRL's TBS fields after the optional version, its encoded TBS signature algorithm and outer signature
    algorithm, and the CRL's DER."""
    _, start, end = der_single(crl)
    outer = der_children(crl, start, end)
    if len(outer) != 3:
        raise ValueError("a CRL has three parts")
    fields = der_children(crl, outer[0][1], outer[0][2])
    offset = 1 if fields[0][0] == 0x02 else 0
    return fields[offset:], crl[fields[offset][3]:fields[offset][4]], outer[1]


def crl_fields(crl):
    """Returns the CRL's encoded issuer, its thisUpdate, its entries as (serial, revoked from, issuers, removed) and its
    CRL extensions as (oid, critical, value start, value end)."""
    fields, _, _ = crl_parts(crl)
    issuer = crl[fields[1][3]:fields[1][4]]
    this_update = der_time(crl, fields[2][0], fields[2][1], fields[2][2])
    entries, extensions = [], []
    for field in fields[3:]:
        if field[0] == 0xA0:
            for extension in der_children(crl, *der_children(crl, field[1], field[2])[0][1:3]):
                parts = der_children(crl, extension[1], extension[2])
                critical = len(parts) == 3 and parts[1][0] == 0x01 and crl[parts[1][1]:parts[1][2]] != b"\x00"
                extensions.append((der_oid(crl, parts[0]), critical, parts[-1][1], parts[-1][2]))
            continue
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
                    oid = der_oid(crl, extension_parts[0])
                    value = extension_parts[-1]
                    inner = der_children(crl, value[1], value[2])[0]
                    if oid == "2.5.29.24":
                        revoked_from = min(revoked_from, der_time(crl, inner[0], inner[1], inner[2]))
                    elif oid == "2.5.29.21":
                        removed = crl[inner[1]:inner[2]] == b"\x08"
                    elif oid == "2.5.29.29":
                        # Applies to this and every following entry; without a directoryName the issuer is unknown.
                        issuers = [crl[name[1]:name[2]] for name in der_children(crl, inner[1], inner[2]) if name[0] == 0xA4]
            entries.append((serial, revoked_from, list(issuers), removed))
    return issuer, this_update, entries, extensions


def crl_complete_for(crl, extensions, locations):
    """A CRL is complete for a distribution point when it is no delta CRL, carries no critical extension besides the
    issuing distribution point and the CRL number, and an issuing distribution point, if present, covers all reasons,
    is restricted neither to CA nor to attribute certificates, is not indirect, and names one of the locations."""
    seen = set()
    for oid, critical, start, end in extensions:
        if oid in seen or oid == OID_DELTA_CRL_INDICATOR:
            return False
        seen.add(oid)
        if oid == OID_ISSUING_DISTRIBUTION_POINT:
            _, point_start, point_end, _ = der_read(crl, start)
            for part in der_children(crl, point_start, point_end):
                tag, content = part[0], crl[part[1]:part[2]]
                if tag == 0xA0:
                    name = der_children(crl, part[1], part[2])
                    named = name and name[0][0] == 0xA0 and any(
                        general_name[0] == 0x86 and normalize_uri(crl[general_name[1]:general_name[2]].decode("ascii")) in locations
                        for general_name in der_children(crl, name[0][1], name[0][2]))
                    if not named:
                        return False
                elif tag == 0x83 or (tag in (0x82, 0x84, 0x85) and content != b"\x00"):
                    return False
        elif oid != OID_CRL_NUMBER and critical:
            return False
    return True


def oid_hash(data, algorithm):
    """The SHA-2 hash an AlgorithmIdentifier names, or None."""
    parts = der_children(data, algorithm[1], algorithm[2])
    return der_oid(data, parts[0]) if parts and der_oid(data, parts[0]) in SHA2_DIGESTS else None


def accepted_pss(crl, parameters):
    """RSASSA-PSS parameters are accepted in the form .NET verifies: an explicit SHA-2 hash, MGF1 with the same hash,
    a salt as long as the hash and the default trailer."""
    fields = {part[0]: part for part in der_children(crl, parameters[1], parameters[2])}
    if 0xA0 not in fields or 0xA1 not in fields or 0xA2 not in fields:
        return False
    hash_oid = oid_hash(crl, der_children(crl, fields[0xA0][1], fields[0xA0][2])[0])
    mask = der_children(crl, *der_children(crl, fields[0xA1][1], fields[0xA1][2])[0][1:3])
    salt = der_children(crl, fields[0xA2][1], fields[0xA2][2])[0]
    trailer = der_children(crl, fields[0xA3][1], fields[0xA3][2])[0] if 0xA3 in fields else None
    return (hash_oid is not None and der_oid(crl, mask[0]) == OID_MGF1 and oid_hash(crl, mask[1]) == hash_oid and
            int.from_bytes(crl[salt[1]:salt[2]], "big") == SHA2_DIGESTS[hash_oid] and
            (trailer is None or int.from_bytes(crl[trailer[1]:trailer[2]], "big") == 1))


def public_key(certificate, directory):
    """Returns the public key algorithm and size of a DER certificate as OpenSSL reports them."""
    path = os.path.join(directory, "key.der")
    with open(path, "wb") as stream:
        stream.write(certificate)
    _, output = openssl("x509", "-inform", "DER", "-in", path, "-noout", "-text")
    algorithm = re.search(r"Public Key Algorithm: (\S+)", output)
    size = re.search(r"Public-Key: \((\d+) bit\)", output)
    return (algorithm.group(1) if algorithm else None), (int(size.group(1)) if size else None)


def adequate_key(certificate, directory):
    """RSA keys need at least 2048 bits and ECDSA keys 256; other key types are not size-checked."""
    algorithm, size = public_key(certificate, directory)
    minimum = {"rsaEncryption": 2048, "rsassaPss": 2048, "id-ecPublicKey": 256}.get(algorithm)
    return minimum is None or (size is not None and size >= minimum)


def crl_signature_accepted(crl, issuer_certificate, directory):
    """Applies the CRL signature policy: the TBS and outer algorithms are equal, the algorithm is RSA PKCS #1 v1.5,
    RSASSA-PSS or ECDSA with SHA-256, -384 or -512, the issuer's key is large enough, and OpenSSL verifies the signature."""
    _, tbs_algorithm, outer_algorithm = crl_parts(crl)
    if tbs_algorithm != crl[outer_algorithm[3]:outer_algorithm[4]]:
        return False
    parts = der_children(crl, outer_algorithm[1], outer_algorithm[2])
    oid = der_oid(crl, parts[0])
    if oid not in CRL_SIGNATURES and not (oid == OID_RSASSA_PSS and len(parts) == 2 and accepted_pss(crl, parts[1])):
        return False
    if not adequate_key(issuer_certificate, directory):
        return False
    crl_path, issuer_path = os.path.join(directory, "crl.der"), os.path.join(directory, "issuer.pem")
    with open(crl_path, "wb") as stream:
        stream.write(crl)
    with open(issuer_path, "w") as stream:
        stream.write(pem(issuer_certificate))
    code, output = openssl("crl", "-inform", "DER", "-in", crl_path, "-CAfile", issuer_path, "-noout")
    return code == 0 and "verify OK" in output


def token_info(token):
    """Decodes an RFC 3161 token (CMS SignedData over a TSTInfo) and returns its policy OID, genTime, signer digest and
    signature algorithms, and whether it carries a countersignature. Raises ValueError when the signed content is not
    exactly one TSTInfo or the token is not exactly one DER value."""
    _, start, end = der_single(token)
    content_info = der_children(token, start, end)
    signed_data = der_children(token, *der_children(token, content_info[1][1], content_info[1][2])[0][1:3])
    encapsulated = der_children(token, signed_data[2][1], signed_data[2][2])
    if der_oid(token, encapsulated[0]) != OID_ID_CT_TSTINFO:
        raise ValueError("the signed content is not a TSTInfo")
    octets = der_children(token, encapsulated[1][1], encapsulated[1][2])[0]
    tst_info = token[octets[1]:octets[2]]
    _, start, end = der_single(tst_info)
    fields = der_children(tst_info, start, end)
    signer_infos = der_children(token, signed_data[-1][1], signed_data[-1][2])
    if signed_data[-1][0] != 0x31 or len(signer_infos) != 1:
        raise ValueError("the token does not have exactly one signer")
    signer = der_children(token, signer_infos[0][1], signer_infos[0][2])
    digest_algorithm = der_oid(token, der_children(token, signer[2][1], signer[2][2])[0])
    rest = signer[4:] if signer[3][0] == 0xA0 else signer[3:]
    signature_algorithm = der_oid(token, der_children(token, rest[0][1], rest[0][2])[0])
    countersigned = False
    for unsigned in (part for part in rest[2:] if part[0] == 0xA1):
        for attribute in der_children(token, unsigned[1], unsigned[2]):
            countersigned |= der_oid(token, der_children(token, attribute[1], attribute[2])[0]) == OID_COUNTERSIGNATURE
    return {
        "policy": der_oid(tst_info, fields[1]),
        "time": der_time(tst_info, fields[4][0], fields[4][1], fields[4][2]),
        "digestAlgorithm": digest_algorithm,
        "signatureAlgorithm": signature_algorithm,
        "countersigned": countersigned,
    }


def pem(der, label="CERTIFICATE"):
    body = base64.encodebytes(der).decode("ascii").replace("\n", "")
    lines = [body[i:i + 64] for i in range(0, len(body), 64)]
    return f"-----BEGIN {label}-----\n" + "\n".join(lines) + f"\n-----END {label}-----\n"


def openssl(*arguments):
    result = subprocess.run(["openssl", *arguments], capture_output=True, text=True)
    return result.returncode, result.stdout + result.stderr


class Trust:
    """Trusted roots and required policies per authority; roots without a name apply to every authority."""

    def __init__(self, roots=None, policies=None):
        self.roots = roots or {}
        self.policies = policies or {}

    def roots_for(self, authority):
        return self.roots.get(None, []) + self.roots.get(authority, [])


def removed_revocation_evidence(entry, chain, confirming):
    """A stored status of Confirmed or Revoked without confirming CRLs, although the chain names distribution points,
    shows that the confirming CRLs were removed."""
    if entry["RevocationStatus"] not in ("Confirmed", "Revoked") or confirming:
        return False
    try:
        return bool(crl_sources(chain))
    except PARSE_ERRORS:
        return False


def verify_timestamp(entry, evidence, digest, trust, grace):
    """Verifies one stored token with OpenSSL and recomputes its revocation status from the confirming CRLs."""
    report = {"authority": entry["Authority"], "valid": False, "failure": None, "revocationStatus": "Unconfirmed", "storedRevocationStatus": entry["RevocationStatus"]}
    roots = trust.roots_for(entry["Authority"])
    if not roots:
        report["failure"] = f"no trust settings are configured for timestamp authority '{entry['Authority']}'"
        return report
    try:
        token = evidence.resolve(entry["TokenHash"])
        chain = [evidence.resolve(item) for item in entry["CertificateChainHashes"]]
        for item in entry["CertificateRevocationListHashes"]:
            evidence.resolve(item)
        confirming = [evidence.resolve(item) for item in entry["ConfirmingRevocationListHashes"]]
    except LookupError as error:
        report["failure"] = str(error)
        return report
    try:
        info = token_info(token)
    except PARSE_ERRORS:
        report["failure"] = "malformed token: the signed content is not exactly one TSTInfo with one signer"
        return report
    token_time = info["time"]
    if (token_time - datetime_ms(entry["TimestampUtc"])) >= datetime.timedelta(milliseconds=1) or token_time < datetime_ms(entry["TimestampUtc"]):
        report["failure"] = "the token's time differs from the stored time"
        return report
    if info["countersigned"]:
        report["failure"] = "the token carries a countersignature besides the authority's signature"
        return report
    policy = trust.policies.get(entry["Authority"])
    if policy is not None and info["policy"] != policy:
        report["failure"] = f"the token is issued under policy {info['policy']}, not the required policy {policy}"
        return report
    if info["digestAlgorithm"] in WEAK_DIGESTS or info["signatureAlgorithm"] in WEAK_SIGNATURES:
        report["failure"] = "the token is signed with a weak algorithm; MD5 and SHA-1 are not accepted"
        return report
    with tempfile.TemporaryDirectory() as directory:
        if not chain or not adequate_key(chain[0], directory):
            report["failure"] = "the signing key is not of the required size"
            return report
        token_path, chain_path, roots_path = (os.path.join(directory, name) for name in ("token.der", "chain.pem", "roots.pem"))
        with open(token_path, "wb") as stream:
            stream.write(token)
        with open(chain_path, "w") as stream:
            stream.write("".join(pem(certificate) for certificate in chain))
        with open(roots_path, "w") as stream:
            for root in roots:
                with open(root) as source:
                    stream.write(source.read())
        code, output = openssl("ts", "-verify", "-digest", digest.hex(), "-in", token_path, "-token_in",
                               "-CAfile", roots_path, "-untrusted", chain_path, "-attime", str(int(token_time.timestamp())))
        if code != 0 or "Verification: OK" not in output:
            report["failure"] = "openssl ts -verify failed: " + output.strip().splitlines()[-1] if output.strip() else "openssl ts -verify failed"
            return report
        if removed_revocation_evidence(entry, chain, confirming):
            report["failure"] = "the stored revocation status is confirmed or revoked, but its confirming CRLs are missing"
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
    except PARSE_ERRORS:
        return "Unconfirmed"
    confirmed = len(sources) == len(confirming)
    entries = []
    for (locations, expected_issuer), crl in zip(sources, confirming):
        try:
            issuer, this_update, crl_entries, extensions = crl_fields(crl)
            issuer_certificate = next((certificate for certificate in chain if certificate_fields(certificate)["subject"] == expected_issuer), None)
            counts = (expected_issuer is not None and issuer == expected_issuer and this_update >= token_time + grace and issuer_certificate is not None and
                      crl_complete_for(crl, extensions, locations) and crl_signature_accepted(crl, issuer_certificate, directory))
        except PARSE_ERRORS:
            counts = False
        if not counts:
            confirmed = False
            continue
        entries += crl_entries
    for certificate in chain[:-1]:
        fields = certificate_fields(certificate)
        for serial, revoked_from, issuers, removed in entries:
            if not removed and serial == fields["serial"] and (not issuers or fields["issuer"] in issuers) and revoked_from <= token_time:
                return "Revoked"
    return "Confirmed" if confirmed else "Unconfirmed"


def verify_anchor(anchor, evidence, trust, grace):
    digest_valid = anchor_digest(anchor["FormatVersion"], anchor["Sequence"], anchor["PreviousDigest"], anchor["MerkleRoot"]) == anchor["AnchorDigest"]
    timestamps = [verify_timestamp(entry, evidence, anchor["AnchorDigest"], trust, grace) for entry in anchor["Timestamps"]]
    return {"sequence": anchor["Sequence"], "digestValid": digest_valid, "timestamps": timestamps,
            "verified": digest_valid and any(timestamp["valid"] for timestamp in timestamps)}


# --- Commands -------------------------------------------------------------------------------------------------------


def stream_documents(documents, aggregate_id, malformed=None):
    """The documents of one stream. Documents that cannot be read are skipped and listed in malformed."""
    selected = []
    for index, document in enumerate(documents):
        try:
            if get_binary(document, "AggregateId", SUBTYPE_UUID) == aggregate_id:
                selected.append(document)
        except PARSE_ERRORS as error:
            if malformed is not None:
                malformed.append({"collection": "events", "index": index, "error": str(error)})
    return selected


def parse_trust(trusted_roots, policies):
    """Parses [NAME=]PEM roots and NAME=OID policies. An argument that names an existing file is a root for every
    authority."""
    roots, required = {}, {}
    for argument in trusted_roots:
        name, separator, path = argument.partition("=")
        if os.path.exists(argument) or not separator:
            roots.setdefault(None, []).append(argument)
        else:
            roots.setdefault(name, []).append(path)
    for argument in policies:
        name, separator, oid = argument.partition("=")
        if not separator or not name or not oid:
            raise ValueError(f"--policy expects NAME=OID, not '{argument}'")
        required[name] = oid
    return Trust(roots, required)


def parse_trusted_head(text):
    sequence, separator, digest = (text or "").partition(":")
    if not text:
        return None
    if not separator or not sequence.isdigit() or int(sequence) < 1 or len(digest) != 64:
        raise ValueError("--trusted-head expects SEQUENCE:DIGESTHEX with a positive sequence number and a SHA-256 digest")
    return int(sequence), bytes.fromhex(digest)


def check(arguments):
    aggregate_id = guid_bytes(arguments.aggregate_id)
    malformed = []
    events = stream_documents(read_documents(arguments.events), aggregate_id, malformed)
    report = {"stream": None, "anchorChain": None, "anchors": [], "malformed": malformed}
    if not arguments.anchors:
        report["stream"] = verify_stream(events, aggregate_id)
        return report, report["stream"]["intact"]

    trust = parse_trust(arguments.trusted_root, arguments.policy)
    trusted_head = parse_trusted_head(arguments.trusted_head)
    anchor_documents = read_documents(arguments.anchors)
    leaf_documents = read_documents(arguments.leaves)
    anchors = {anchor["Id"]: anchor for anchor in (read_anchor(document) for document in anchor_documents) if anchor}
    evidence = Evidence(read_documents(arguments.evidence), read_documents(arguments.evidence_chunks) if arguments.evidence_chunks else [])
    malformed += evidence.malformed
    grace = datetime.timedelta(hours=arguments.grace_hours)
    leaves = []
    for index, document in enumerate(leaf_documents):
        leaf = read_leaf(document)
        if leaf is None:
            malformed.append({"collection": "leaves", "index": index, "error": "the leaf cannot be read"})
            continue
        anchor = anchors.get(leaf["AnchorId"])
        if (leaf["AggregateType"] == arguments.aggregate_type and leaf["AggregateId"] == aggregate_id and
                anchor is not None and anchor["Status"] == "Completed" and anchor["Sequence"] == leaf["AnchorSequence"]):
            leaves.append((anchor, leaf))
    leaves.sort(key=lambda item: item[0]["Sequence"])
    report["anchorChain"] = verify_anchor_chain(anchor_documents, leaf_documents, trusted_head)
    report["stream"] = verify_stream(events, aggregate_id, [(leaf["Version"], leaf["Hash"], proves_inclusion(leaf, anchor)) for anchor, leaf in leaves])
    ok = report["stream"]["intact"] and report["anchorChain"]["intact"]
    for anchor, leaf in leaves:
        anchor_report = verify_anchor(anchor, evidence, trust, grace)
        anchor_report["inclusionValid"] = proves_inclusion(leaf, anchor)
        anchor_report["leafVersion"] = leaf["Version"]
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
    leaf_documents = read_documents(os.path.join(directory, "leaves.bson"))
    expect("anchor chain", verify_anchor_chain(anchor_documents, leaf_documents), expected_anchors["chain"])
    evidence = Evidence(read_documents(os.path.join(directory, "evidence.bson")), [])
    trust = Trust({None: [os.path.join(directory, name) for name in expected_anchors["trustedRoots"]]})
    grace = datetime.timedelta(hours=expected_anchors["graceHours"])
    for document, expected in zip(sorted(anchor_documents, key=lambda d: get(d, "Sequence", T_INT32, T_INT64)), expected_anchors["anchors"]):
        anchor = read_anchor(document)
        expect(f"anchor digest {anchor['Sequence']}", anchor_digest(1, anchor["Sequence"], anchor["PreviousDigest"], anchor["MerkleRoot"]).hex(), expected["digest"])
        result = verify_anchor(anchor, evidence, trust, grace)
        expect(f"anchor {anchor['Sequence']}", [(t["valid"], t["revocationStatus"]) for t in result["timestamps"]],
               [tuple(item) for item in expected["timestamps"]])
    anchors = {anchor["Id"]: anchor for anchor in (read_anchor(document) for document in anchor_documents)}
    for document in leaf_documents:
        leaf = read_leaf(document)
        expect("anchored leaf inclusion", proves_inclusion(leaf, anchors[leaf["AnchorId"]]), True)

    with open(os.path.join(directory, "evidence-chunked.json")) as stream:
        chunked = json.load(stream)
    item = chunked_item(chunked["length"], chunked["multiplier"], chunked["increment"], chunked["modulus"])
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
    check_parser.add_argument("--trusted-root", action="append", default=[], help="[NAME=]PEM; without a name, for every authority")
    check_parser.add_argument("--policy", action="append", default=[], help="NAME=OID, the policy the authority's tokens must be issued under")
    check_parser.add_argument("--grace-hours", type=float, default=24)
    check_parser.add_argument("--trusted-head", help="SEQUENCE:DIGESTHEX of an anchor kept outside the database")
    arguments = parser.parse_args()
    try:
        if arguments.command == "vectors":
            report, ok = vectors(arguments.directory)
        else:
            if arguments.anchors and not (arguments.leaves and arguments.evidence and arguments.trusted_root):
                parser.error("--anchors needs --leaves, --evidence and at least one --trusted-root")
            report, ok = check(arguments)
    except (OSError, *PARSE_ERRORS) as error:
        json.dump({"error": str(error)}, sys.stdout, indent=2)
        sys.stdout.write("\n")
        return 2
    json.dump(report, sys.stdout, indent=2, default=lambda value: value.hex() if isinstance(value, bytes) else str(value))
    sys.stdout.write("\n")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
