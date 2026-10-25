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
import decimal
import hashlib
import ipaddress
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
T_BOOLEAN, T_DATETIME, T_NULL, T_INT32, T_INT64, T_DECIMAL128 = 0x08, 0x09, 0x0A, 0x10, 0x12, 0x13
SUBTYPE_GENERIC, SUBTYPE_UUID = 0x00, 0x04
ANCHOR_STATUSES = ("Preparing", "AwaitingTimestamp", "Completed", "Discarded")
REVOCATION_STATUSES = ("Unconfirmed", "Confirmed", "Revoked")
OPENSSL_TIMEOUT_SECONDS = 60

# Errors that malformed BSON or DER input raises while it is parsed; UnicodeDecodeError is a ValueError. RecursionError
# backs up the nesting limits below for input files that MongoDB itself would not have stored.
PARSE_ERRORS = (ValueError, struct.error, IndexError, RecursionError)
# The deepest nesting accepted: MongoDB's limit for documents, and a bound far above real tokens for DER.
MAX_BSON_DEPTH = 100
MAX_DER_DEPTH = 64


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


def raw_elements(document):
    """Yields (name bytes, type, value bytes, element start, element end) for the top-level elements of a document,
    without decoding the names."""
    length = struct.unpack_from("<i", document, 0)[0]
    if length != len(document) or length < 5 or document[-1] != 0:
        raise BsonError("document length does not match")
    position = 4
    while document[position] != 0:
        bson_type = document[position]
        name_end = _cstring_end(document, position + 1)
        value_start = name_end + 1
        value_size = _value_size(document, bson_type, value_start)
        value_end = value_start + value_size
        if value_size < 0 or value_end > length - 1:
            raise BsonError("element exceeds the document")
        yield document[position + 1:name_end], bson_type, document[value_start:value_end], position, value_end
        position = value_end
    if position != length - 1:
        raise BsonError("trailing bytes in document")


def elements(document):
    """Yields (name, type, value bytes, element start, element end) for the top-level elements of a document."""
    for name, bson_type, value, start, end in raw_elements(document):
        yield name.decode("utf-8"), bson_type, value, start, end


def first(document, name):
    for element_name, bson_type, value, _, _ in elements(document):
        if element_name == name:
            return bson_type, value
    return None


def first_raw(document, name):
    """Like first, but compares the names as bytes, as MongoDB's query and sort engine does, so a name elsewhere in the
    document that is not valid UTF-8 does not matter."""
    target = name.encode("utf-8")
    for element_name, bson_type, value, _, _ in raw_elements(document):
        if element_name == target:
            return bson_type, value
    return None


def query_values(found):
    """The values a MongoDB equality or range query compares for an element: the element itself and, for an array, each
    of its items. A document whose array holds the queried value is therefore selected, and then fails to read."""
    if found is None:
        return []
    return [found] + (array_items(found[1]) if found[0] == T_ARRAY else [])


def selects_uuid(document, name, expected):
    """Whether a query for a standard UUID on an element selects the document."""
    return any(bson_type == T_BINARY and value[4] == SUBTYPE_UUID and value[5:] == expected for bson_type, value in query_values(first_raw(document, name)))


def selects_string(document, name, expected):
    """Whether a query for a string on an element selects the document."""
    target = expected.encode("utf-8")
    return any(bson_type == T_STRING and value[4:-1] == target for bson_type, value in query_values(first_raw(document, name)))


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


def has_distinct_names(document, nested=False, depth=0):
    """Whether no two elements of a document share a name, and with nested also none of its embedded documents and
    arrays, as the .NET driver requires when it reads a document. Nesting beyond MongoDB's limit counts as malformed."""
    if depth > MAX_BSON_DEPTH:
        return False
    names = set()
    for name, bson_type, value, _, _ in elements(document):
        if name in names:
            return False
        names.add(name)
        if nested and bson_type in (T_DOCUMENT, T_ARRAY) and not has_distinct_names(value, True, depth + 1):
            return False
    return True


def decimal128_value(value):
    """The numeric value of a decimal128 for ordering: a Decimal, or None for NaN."""
    bits = int.from_bytes(value, "little")
    sign = -1 if bits >> 127 else 1
    if (bits >> 122) & 0x1F == 0x1F:
        return None
    if (bits >> 122) & 0x1F == 0x1E:
        return decimal.Decimal(sign) * decimal.Decimal("Infinity")
    if (bits >> 125) & 0x03 == 0x03:
        exponent, coefficient = (bits >> 111) & 0x3FFF, (0x04 << 111) | (bits & ((1 << 111) - 1))
    else:
        exponent, coefficient = (bits >> 113) & 0x3FFF, bits & ((1 << 113) - 1)
    if coefficient > 10 ** 34 - 1:
        coefficient = 0
    return decimal.Decimal(sign * coefficient).scaleb(exponent - 6176, decimal.Context(prec=40))


def sort_key(found, in_array=False):
    """The position of an element value, or of a missing one (None), in MongoDB's ascending sort: missing, null and
    empty arrays before numbers, numbers by value with NaN first, every other type after them, and a non-empty array
    by its smallest element, in which a nested array counts as an array. Ties keep the input order."""
    if found is None:
        return (1,)
    bson_type, value = found
    if bson_type == 0xFF:
        return (0,)
    if bson_type in (T_NULL, 0x06):
        return (1,)
    if bson_type == T_ARRAY:
        if in_array:
            return (3,)
        return min((sort_key(item, True) for item in array_items(value)), default=(1,))
    if bson_type in (T_INT32, T_INT64, T_DOUBLE, T_DECIMAL128):
        number = decimal128_value(value) if bson_type == T_DECIMAL128 else decode(bson_type, value)
        if number is None or number != number:
            return (2, 0, 0)
        return (2, 1, number)
    return (3,)


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


EARLIEST = datetime.datetime.min.replace(tzinfo=datetime.timezone.utc)
LATEST = datetime.datetime.max.replace(tzinfo=datetime.timezone.utc)


def datetime_ms(milliseconds):
    """A BSON date; dates outside the years 1 to 9999 are clamped to that range, as .NET reads them."""
    try:
        return datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc) + datetime.timedelta(milliseconds=milliseconds)
    except OverflowError:
        return EARLIEST if milliseconds < 0 else LATEST


def later(moment, delta):
    """A time plus a duration, or the latest time when the sum exceeds the year 9999."""
    try:
        return moment + delta
    except OverflowError:
        return LATEST


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
    subtype 0x00, PersonalData an array of documents with a string Path. An erased salt is absent, not null. The document
    and each entry must not repeat an element name."""
    try:
        if not has_distinct_names(value):
            return None
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
                if bson_type != T_DOCUMENT or not has_distinct_names(entry):
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
        found = first_raw(document, "Version")
        return as_version(*found) if found else None
    except PARSE_ERRORS:
        return None


def version_sort_key(document):
    try:
        return sort_key(first_raw(document, "Version"))
    except PARSE_ERRORS:
        return (3,)


def has_readable_names(document):
    """Whether every top-level element name of a document is valid UTF-8."""
    try:
        for _ in elements(document):
            pass
        return True
    except PARSE_ERRORS:
        return False


def compare_with_leaves(events, leaves, first_unreadable, broken_version):
    """Compares the verified events with the stream's leaves below a break, if any: the lowest version at which a leaf
    cannot be read, does not prove its inclusion, names a missing version or a different hash, the first leaf's on a
    tie, or None."""
    lowest = None
    if first_unreadable is not None and (broken_version is None or first_unreadable < broken_version):
        lowest = (first_unreadable, "AnchoredLeafInvalid")
    for leaf_version, leaf_hash, proves_inclusion in leaves:
        if broken_version is not None and leaf_version >= broken_version:
            continue
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
    return lowest


def verify_stream(documents, aggregate_id, leaves=None, first_unreadable=None):
    """Verifies the chain of one stream; documents are its events in any order, verified in MongoDB's order of their
    versions. Leaves are (version, hash, proves inclusion) triples of completed anchors in anchor sequence order, and
    first_unreadable is the lowest version of a stream leaf that cannot be read; both judge the verified events. A
    broken stream reports the events verified before the break, with their seal-mode transitions and personal data."""
    ordered = sorted(documents, key=version_sort_key)
    report = {"intact": True, "firstBrokenVersion": None, "failure": None, "events": [], "sealModeTransitions": [], "unrecordedErasures": []}
    previous_hash, recorded, erased = None, set(), []

    def finish(version=None, failure=None):
        events = report["events"]
        for index in range(1, len(events)):
            if events[index - 1]["sealMode"] == "Append" and events[index]["sealMode"] == "Retroactive":
                report["sealModeTransitions"].append(events[index]["version"])
        for event in events:
            for entry in event["personalData"]:
                entry["status"] = "Present" if not entry["erased"] else ("ErasedWithRecord" if (event["version"], entry["path"]) in recorded else "ErasedWithoutRecord")
                del entry["erased"]
        earlier = compare_with_leaves(events, leaves or [], first_unreadable, version)
        if earlier is not None:
            version, failure = earlier
        if failure is not None:
            report.update(intact=False, firstBrokenVersion=version, failure=failure)
        else:
            report["unrecordedErasures"] = [[version, path] for version, path in erased if (version, path) not in recorded]
        return report

    for expected, document in enumerate(ordered, start=1):
        if not has_readable_names(document):
            return finish(expected, "MalformedIntegrity")
        if read_version(document) != expected:
            return finish(expected, "VersionGap")
        try:
            if previous_hash is None:
                try:
                    aggregate_type = get(document, "AggregateType", T_STRING)
                except UnicodeDecodeError:
                    aggregate_type = None
                if aggregate_type is None:
                    return finish(expected, "MissingAggregateType")
                previous_hash = genesis(aggregate_type, aggregate_id)
            failure, integrity, statuses = verify_event(document, previous_hash)
            if not failure and is_erasure_event(document):
                recorded |= erasure_records(document, expected)
        except PARSE_ERRORS:
            failure = "MalformedIntegrity"
        if failure:
            return finish(expected, failure)
        report["events"].append({
            "version": expected,
            "hash": integrity["Hash"].hex(),
            "previousHash": integrity["PreviousHash"].hex(),
            "sealMode": integrity["SealMode"],
            "personalData": [{"path": path, "erased": was_erased} for path, was_erased in statuses],
        })
        erased += [(expected, path) for path, was_erased in statuses if was_erased]
        previous_hash = integrity["Hash"]
    return finish()


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


class Malformed(ValueError):
    """An anchor, leaf or timestamp entry element that is missing or not of its exact type."""


def element(document, name, *types):
    """The decoded value of a required element of one of the given types."""
    found = first(document, name)
    if found is None or found[0] not in types:
        raise Malformed(f"{name} is missing or of another type")
    return decode(*found)


def optional(document, name, read):
    """Reads an optional element with a reader of (document, name): None when it is absent."""
    return None if first(document, name) is None else read(document, name)


def uuid_element(document, name):
    found = first(document, name)
    if found is None or found[0] != T_BINARY or found[1][4] != SUBTYPE_UUID or len(found[1]) != 5 + 16:
        raise Malformed(f"{name} is not a standard UUID")
    return found[1][5:]


def binary_element(document, name):
    found = first(document, name)
    if found is None or found[0] != T_BINARY or found[1][4] != SUBTYPE_GENERIC:
        raise Malformed(f"{name} is not generic binary data")
    return found[1][5:]


def binaries_element(document, name):
    items = element(document, name, T_ARRAY)
    values = array_items(items)
    if any(bson_type != T_BINARY or value[4] != SUBTYPE_GENERIC for bson_type, value in values):
        raise Malformed(f"{name} holds other values than generic binary data")
    return [value[5:] for _, value in values]


def documents_element(document, name):
    items = array_items(element(document, name, T_ARRAY))
    if any(bson_type != T_DOCUMENT for bson_type, _ in items):
        raise Malformed(f"{name} holds other values than documents")
    return [value for _, value in items]


def name_element(document, name, names):
    value = element(document, name, T_STRING)
    if value not in names:
        raise Malformed(f"{name} is not one of {', '.join(names)}")
    return value


def optional_date(document, name):
    """A date that may be absent or null."""
    found = first(document, name)
    return None if found is None or found[0] == T_NULL else element(document, name, T_DATETIME)


def read_timestamp(entry):
    """Reads a timestamp entry with the exact types the library writes and reads."""
    timestamp = {
        "Authority": element(entry, "Authority", T_STRING),
        "TokenHash": binary_element(entry, "TokenHash"),
        "CertificateChainHashes": binaries_element(entry, "CertificateChainHashes"),
        "CertificateRevocationListHashes": binaries_element(entry, "CertificateRevocationListHashes"),
        "TimestampUtc": element(entry, "TimestampUtc", T_DATETIME),
        "RevocationStatus": name_element(entry, "RevocationStatus", REVOCATION_STATUSES),
        "ConfirmingRevocationListHashes": optional(entry, "ConfirmingRevocationListHashes", binaries_element) or [],
    }
    element(entry, "ObtainedUtc", T_DATETIME)
    optional_date(entry, "RevocationStatusUtc")
    optional_date(entry, "ConfirmationNotBeforeUtc")
    if (optional(entry, "ConfirmationFailureCount", lambda document, name: element(document, name, T_INT32)) or 0) > 0:
        element(entry, "LastConfirmationFailure", T_STRING)
        element(entry, "LastConfirmationFailureUtc", T_DATETIME)
    return timestamp


def read_anchor(document):
    """Reads an anchor document with the exact types the library writes and reads; returns None when it is malformed,
    which includes a repeated element name."""
    try:
        if not has_distinct_names(document, True):
            return None
        anchor = {
            "Id": uuid_element(document, "_id"),
            "Status": name_element(document, "Status", ANCHOR_STATUSES),
            "Sequence": element(document, "Sequence", T_INT64, T_INT32),
            "FormatVersion": element(document, "FormatVersion", T_INT32),
            "HashAlgorithm": element(document, "HashAlgorithm", T_STRING),
            "PreviousDigest": binary_element(document, "PreviousDigest"),
            "MerkleRoot": binary_element(document, "MerkleRoot"),
            "AnchorDigest": binary_element(document, "AnchorDigest"),
            "LeafCount": element(document, "LeafCount", T_INT32),
            "Timestamps": [read_timestamp(entry) for entry in optional(document, "Timestamps", documents_element) or []],
        }
        for name in ("PeriodStartUtc", "PeriodEndUtc", "OldestCandidateUtc", "CreatedUtc"):
            element(document, name, T_DATETIME)
        for failure in optional(document, "AuthorityFailures", documents_element) or []:
            element(failure, "Authority", T_STRING)
            element(failure, "FailureCount", T_INT32)
            element(failure, "LastFailure", T_STRING)
            element(failure, "LastFailureUtc", T_DATETIME)
        optional_date(document, "CompletedUtc")
        return anchor
    except PARSE_ERRORS:
        return None


def read_leaf(document):
    """Reads a leaf document with the exact types the library writes and reads; returns None when it is malformed. The
    version is a whole number of at least 1."""
    try:
        if not has_distinct_names(document, True):
            return None
        leaf = {
            "AnchorId": uuid_element(document, "AnchorId"),
            "AnchorSequence": element(document, "AnchorSequence", T_INT64, T_INT32),
            "Index": element(document, "Index", T_INT32),
            "AggregateType": element(document, "AggregateType", T_STRING),
            "AggregateId": uuid_element(document, "AggregateId"),
            "Version": element(document, "Version", T_INT64, T_INT32),
            "Hash": binary_element(document, "Hash"),
            "Proof": binaries_element(document, "Proof"),
        }
        return leaf if leaf["Version"] >= 1 else None
    except PARSE_ERRORS:
        return None


def leaf_hash_of(leaf):
    return hash_leaf(encode_leaf(leaf["AggregateType"], leaf["AggregateId"], leaf["Version"], leaf["Hash"]))


def proves_inclusion(leaf, anchor):
    return len(leaf["Hash"]) == 32 and verify_inclusion(leaf_hash_of(leaf), leaf["Index"], anchor["LeafCount"], leaf["Proof"], anchor["MerkleRoot"])


def leaf_documents_by_anchor(leaf_documents):
    """Groups leaf documents by the standard-UUID anchor identifiers a query for an anchor's leaves selects them by,
    including the UUIDs in an AnchorId array; others belong to no anchor."""
    groups = {}
    for document in leaf_documents:
        try:
            anchor_ids = {value[5:] for bson_type, value in query_values(first_raw(document, "AnchorId"))
                          if bson_type == T_BINARY and value[4] == SUBTYPE_UUID and len(value) == 5 + 16}
        except PARSE_ERRORS:
            anchor_ids = set()
        for anchor_id in anchor_ids:
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
    """Verifies the committed anchors, those with a Sequence element, in MongoDB's order of their sequence numbers:
    contiguous sequence numbers, a supported format, previous digests, recomputed digests, roots recomputed from the
    stored leaves and, for every anchor but the newest and every anchor up to a trusted head, the completed status. A
    trusted head (sequence, digest) must be contained unchanged."""
    anchors = []
    for document in anchor_documents:
        try:
            found = first_raw(document, "Sequence")
            if found is None:
                continue
            key = sort_key(found)
        except PARSE_ERRORS:
            key = (3,)
        anchors.append((key, document))
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
        # The completion hook only sees completed anchors, so every anchor up to a trusted head was completed.
        if (anchor["Status"] not in ("AwaitingTimestamp", "Completed") or
                (trusted_head is not None and anchor["Sequence"] <= trusted_head[0] and anchor["Status"] != "Completed") or
                anchor["FormatVersion"] != 1 or anchor["HashAlgorithm"] != ALGORITHM or len(anchor["AnchorDigest"]) != 32):
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


def int32_value(found, name):
    """A stored 32-bit integer: an int32, or an int64 or integral double within the int32 range."""
    if found is None or found[0] not in (T_INT32, T_INT64, T_DOUBLE):
        raise Malformed(f"{name} is missing or not a number")
    number = decode(*found)
    if number != number or not -2 ** 31 <= number < 2 ** 31 or number != int(number):
        raise Malformed(f"{name} is not a 32-bit integer")
    return int(number)


def evidence_int(document, name):
    """An evidence length or count, which the library reads as int32_value does."""
    return int32_value(first(document, name), name)


class Evidence:
    """Content-addressed evidence: items by SHA-256, inline or split into chunks of EVIDENCE_CHUNK_SIZE bytes. An item
    is found by its generic-binary _id, a chunk by an _id of exactly Hash and Index in this order. Documents that cannot
    be read or repeat an element name are skipped and listed in malformed; an item whose document is malformed counts as
    missing."""

    def __init__(self, evidence_documents, chunk_documents):
        self.items, self.chunks, self.malformed = {}, {}, []
        for index, document in enumerate(evidence_documents):
            try:
                if not has_distinct_names(document, True):
                    raise Malformed("the document repeats an element name")
                item_hash = get_binary(document, "_id", SUBTYPE_GENERIC)
                if item_hash is not None:
                    self.items.setdefault(item_hash, document)
            except PARSE_ERRORS as error:
                self.malformed.append({"collection": "evidence", "index": index, "error": str(error)})
        for index, document in enumerate(chunk_documents):
            try:
                if not has_distinct_names(document, True):
                    raise Malformed("the document repeats an element name")
                chunk_id = get(document, "_id", T_DOCUMENT)
                parts = [(name, bson_type, value) for name, bson_type, value, _, _ in elements(chunk_id)] if chunk_id is not None else []
                if [name for name, _, _ in parts] == ["Hash", "Index"] and parts[0][1] == T_BINARY and parts[0][2][4] == SUBTYPE_GENERIC:
                    self.chunks.setdefault((parts[0][2][5:], int32_value(parts[1][1:], "Index")), document)
            except PARSE_ERRORS as error:
                self.malformed.append({"collection": "evidenceChunks", "index": index, "error": str(error)})

    def resolve(self, item_hash):
        document = self.items.get(item_hash)
        if document is None:
            raise LookupError(f"evidence {item_hash.hex()} is missing")
        try:
            length = evidence_int(document, "Length")
            data = evidence_data(document)
            if data is None:
                count = evidence_int(document, "ChunkCount")
                # A chunked item is longer than one chunk and has exactly as many chunks as its length needs.
                if length <= EVIDENCE_CHUNK_SIZE or count != (length - 1) // EVIDENCE_CHUNK_SIZE + 1:
                    raise Malformed("the chunk count does not match the length")
                chunks = [self.chunks.get((item_hash, index)) for index in range(count)]
                if any(chunk is None for chunk in chunks):
                    raise LookupError(f"a chunk of evidence {item_hash.hex()} is missing")
                parts = [evidence_data(chunk) for chunk in chunks]
                if any(part is None for part in parts):
                    raise Malformed("a chunk holds no data")
                data = b"".join(parts)
        except PARSE_ERRORS:
            raise LookupError(f"evidence {item_hash.hex()} is malformed") from None
        if len(data) != length or hashlib.sha256(data).digest() != item_hash:
            raise LookupError(f"evidence {item_hash.hex()} does not match its hash")
        return data


def evidence_data(document):
    """The data of an evidence item or chunk, or None when it has none. Data of another type or subtype, old binary data
    of subtype 0x02 included, is malformed."""
    found = first(document, "Data")
    if found is None:
        return None
    if found[0] != T_BINARY or found[1][4] != SUBTYPE_GENERIC:
        raise Malformed("the evidence data is not generic binary data")
    return found[1][5:]


def chunked_item(length, multiplier, increment, modulus):
    """The deterministic evidence item of the test vectors: byte i is (i * multiplier + increment) mod modulus."""
    return bytes((index * multiplier + increment) % modulus for index in range(length))


# --- DER, certificates, CRLs and tokens -----------------------------------------------------------------------------


OID_ID_CT_TSTINFO = "1.2.840.113549.1.9.16.1.4"
OID_COUNTERSIGNATURE = "1.2.840.113549.1.9.6"
OID_DELTA_CRL_INDICATOR = "2.5.29.27"
OID_ISSUING_DISTRIBUTION_POINT = "2.5.29.28"
OID_CRL_NUMBER = "2.5.29.20"
OID_CRL_DISTRIBUTION_POINTS = "2.5.29.31"
OID_BASIC_CONSTRAINTS = "2.5.29.19"
OID_KEY_USAGE = "2.5.29.15"
OID_EXTENDED_KEY_USAGE = "2.5.29.37"
OID_SUBJECT_KEY_IDENTIFIER = "2.5.29.14"
OID_REASON_CODE = "2.5.29.21"
OID_INVALIDITY_DATE = "2.5.29.24"
OID_CERTIFICATE_ISSUER = "2.5.29.29"
OID_TIME_STAMPING = "1.3.6.1.5.5.7.3.8"
OID_SHA256 = "2.16.840.1.101.3.4.2.1"
OID_RSA = "1.2.840.113549.1.1.1"
OID_RSASSA_PSS = "1.2.840.113549.1.1.10"
OID_EC_PUBLIC_KEY = "1.2.840.10045.2.1"
OID_MGF1 = "1.2.840.113549.1.1.8"
SHA2_DIGESTS = {OID_SHA256: 32, "2.16.840.1.101.3.4.2.2": 48, "2.16.840.1.101.3.4.2.3": 64}
# MD2, MD4, MD5 and SHA-1, which are not collision resistant, and the signature algorithms that name them.
WEAK_DIGESTS = {"1.2.840.113549.2.2", "1.2.840.113549.2.4", "1.2.840.113549.2.5", "1.3.14.3.2.26"}
WEAK_SIGNATURES = {"1.2.840.113549.1.1.2", "1.2.840.113549.1.1.3", "1.2.840.113549.1.1.4", "1.2.840.113549.1.1.5", "1.3.14.3.2.29",
                   "1.2.840.10045.4.1", "1.2.840.10040.4.3"}
# The CRL signature algorithms that are accepted besides RSASSA-PSS: RSA PKCS #1 v1.5, whose parameters are NULL or
# absent, and ECDSA without parameters, each with SHA-256, -384 and -512.
RSA_CRL_SIGNATURES = {"1.2.840.113549.1.1.11", "1.2.840.113549.1.1.12", "1.2.840.113549.1.1.13"}
ECDSA_CRL_SIGNATURES = {"1.2.840.10045.4.3.2", "1.2.840.10045.4.3.3", "1.2.840.10045.4.3.4"}
# The sizes of named elliptic curves: P-256, P-384, P-521, secp256k1 and the brainpool curves of 256, 384 and 512 bits.
CURVE_SIZES = {"1.2.840.10045.3.1.7": 256, "1.3.132.0.34": 384, "1.3.132.0.35": 521, "1.3.132.0.10": 256, "1.3.36.3.3.2.8.1.1.7": 256,
               "1.3.36.3.3.2.8.1.1.11": 384, "1.3.36.3.3.2.8.1.1.13": 512}
MINIMUM_KEY_SIZES = {OID_RSA: 2048, OID_RSASSA_PSS: 2048, OID_EC_PUBLIC_KEY: 256}
DER_NULL = b"\x05\x00"


class InvalidSources(ValueError):
    """A certificate names CRL distribution points, but none with an HTTP location that covers all reasons."""


def der_read(data, position=0):
    """Reads one DER value: returns (tag, content start, content end, value end). Only single-byte tags and minimal
    definite lengths are accepted."""
    tag = data[position]
    if tag & 0x1F == 0x1F:
        raise ValueError("multi-byte DER tags are not supported")
    length = data[position + 1]
    start = position + 2
    if length & 0x80:
        count = length & 0x7F
        encoded = data[start:start + count]
        if count == 0 or count > 4 or len(encoded) != count:
            raise ValueError("unsupported DER length")
        length = int.from_bytes(encoded, "big")
        if encoded[0] == 0 or length < 0x80:
            raise ValueError("the DER length is not minimal")
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


def der_only(data, tag):
    """The content of data that is exactly one DER value with the given tag."""
    found, start, end = der_single(data)
    if found != tag:
        raise ValueError(f"expected the DER tag 0x{tag:02x}")
    return data[start:end]


def require_der(data, start=0, end=None, depth=0):
    """Requires the values from start to end to use only definite minimal lengths and to encode no universal type but
    SEQUENCE and SET as constructed, as the library requires of a token, nested no deeper than MAX_DER_DEPTH."""
    if depth > MAX_DER_DEPTH:
        raise ValueError("the DER values are nested too deeply")
    for tag, content_start, content_end, _, _ in der_children(data, start, len(data) if end is None else end):
        if tag & 0x20:
            if tag & 0xC0 == 0 and tag not in (0x30, 0x31):
                raise ValueError("a universal primitive type is encoded as constructed")
            require_der(data, content_start, content_end, depth + 1)


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


def der_boolean(data, child):
    """A DER BOOLEAN, whose content is 0x00 or 0xFF."""
    content = data[child[1]:child[2]]
    if content not in (b"\x00", b"\xff"):
        raise ValueError("a DER boolean is 0x00 or 0xFF")
    return content == b"\xff"


def der_integer(data, child):
    if child[0] != 0x02 or child[2] == child[1]:
        raise ValueError("expected an integer")
    return int.from_bytes(data[child[1]:child[2]], "big", signed=True)


def der_time(data, tag, start, end):
    if tag not in (0x17, 0x18):
        raise ValueError("expected a time")
    text = data[start:end].decode("ascii").rstrip("Z")
    if tag == 0x17:
        year = int(text[:2])
        text = (str(1900 + year) if year >= 50 else str(2000 + year)) + text[2:]
    whole, _, fraction = text.partition(".")
    moment = datetime.datetime.strptime(whole, "%Y%m%d%H%M%S").replace(tzinfo=datetime.timezone.utc)
    return moment + datetime.timedelta(microseconds=int((fraction + "000000")[:6])) if fraction else moment


def remove_dot_segments(path):
    """Removes the dot segments of an absolute path (RFC 3986, section 5.2.4)."""
    output, segments = [], path.split("/")
    for index in range(1, len(segments)):
        segment, last = segments[index], index == len(segments) - 1
        if segment == "..":
            if output:
                output.pop()
            if last:
                output.append("")
        elif segment == ".":
            if last:
                output.append("")
        else:
            output.append(segment)
    return "/" + "/".join(output)


def is_ipv6_literal(host):
    """Whether the host is a bracketed IPv6 address written with hexadecimal digits, colons and dots only."""
    if not re.fullmatch(r"\[[0-9A-Fa-f:.]+\]", host):
        return False
    try:
        ipaddress.IPv6Address(host[1:-1])
    except ValueError:
        return False
    return True


def normalize_uri(text):
    """Normalizes an HTTP or HTTPS location as the specification defines, on the text as written: the fragment and user
    information are dropped, the scheme and host lower-cased with an IPv6 host keeping its brackets, a default port
    dropped, an empty path made '/' and dot segments removed; the rest of the path and the query stay as written.
    A location counts only when its host is non-empty labels of ASCII letters, digits and hyphens joined by dots, or a
    bracketed IPv6 address of hexadecimal digits, colons and dots, and its port, if any, is a decimal number of at most
    65535. Returns None for other locations."""
    text = text.split("#", 1)[0]
    scheme_end = text.find("://")
    if scheme_end < 0:
        return None
    scheme = text[:scheme_end].lower()
    default_port = {"http": 80, "https": 443}.get(scheme)
    if default_port is None:
        return None
    rest = text[scheme_end + 3:]
    authority_end = min((index for index in (rest.find("/"), rest.find("?")) if index >= 0), default=-1)
    authority = rest if authority_end < 0 else rest[:authority_end]
    path_and_query = "" if authority_end < 0 else rest[authority_end:]
    authority = authority[authority.rfind("@") + 1:]
    if authority.startswith("["):
        close = authority.find("]")
        if close < 0 or (close + 1 < len(authority) and authority[close + 1] != ":"):
            return None
        host, port = authority[:close + 1], authority[close + 2:] if close + 1 < len(authority) else ""
    else:
        host, _, port = authority.partition(":")
    if not (is_ipv6_literal(host) or re.fullmatch(r"[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*", host)):
        return None
    port_suffix = ""
    if port:
        if not re.fullmatch(r"[0-9]+", port) or int(port) > 65535:
            return None
        port_suffix = "" if int(port) == default_port else f":{int(port)}"
    query_start = path_and_query.find("?")
    path = path_and_query if query_start < 0 else path_and_query[:query_start]
    query = "" if query_start < 0 else path_and_query[query_start:]
    return f"{scheme}://{host.lower()}{port_suffix}{remove_dot_segments(path or '/')}{query}"


def covers_all_reasons(bits):
    """ReasonFlags name every revocation reason when bits 1 to 8 are set; bit 0 is unused (RFC 5280, 4.2.1.13). The
    first content byte of the BIT STRING counts its unused bits."""
    flags = bits[1:]
    return len(flags) >= 2 and flags[0] & 0x7F == 0x7F and flags[1] & 0x80 == 0x80


def certificate_fields(certificate):
    """Returns the encoded issuer and subject, the serial number, the encoded SubjectPublicKeyInfo and the extensions
    as (oid, critical, value) of a DER certificate."""
    _, start, end = der_single(certificate)
    parts = der_children(certificate, start, end)
    if len(parts) != 3:
        raise ValueError("a certificate has three parts")
    fields = der_children(certificate, parts[0][1], parts[0][2])
    offset = 1 if fields[0][0] == 0xA0 else 0
    extensions = []
    for field in fields[offset + 6:]:
        if field[0] != 0xA3:
            continue
        for extension in der_children(certificate, *der_children(certificate, field[1], field[2])[0][1:3]):
            extension_parts = der_children(certificate, extension[1], extension[2])
            critical = len(extension_parts) == 3 and der_boolean(certificate, extension_parts[1])
            extensions.append((der_oid(certificate, extension_parts[0]), critical, certificate[extension_parts[-1][1]:extension_parts[-1][2]]))
    return {
        "serial": certificate[fields[offset][1]:fields[offset][2]],
        "issuer": certificate[fields[offset + 2][3]:fields[offset + 2][4]],
        "subject": certificate[fields[offset + 4][3]:fields[offset + 4][4]],
        "publicKey": certificate[fields[offset + 5][3]:fields[offset + 5][4]],
        "extensions": extensions,
    }


def extension_values(fields, oid):
    return [(critical, value) for extension_oid, critical, value in fields["extensions"] if extension_oid == oid]


def key_usage_bits(fields):
    """The key usage flags as (first byte, second byte), or None without a key usage extension."""
    found = extension_values(fields, OID_KEY_USAGE)
    if not found:
        return None
    bits = der_only(found[0][1], 0x03)
    if not bits or bits[0] > 7:
        raise ValueError("a malformed key usage")
    return (bits[1] if len(bits) > 1 else 0), (bits[2] if len(bits) > 2 else 0)


def is_ca(fields):
    """Whether the first basic constraints extension names a CA."""
    found = extension_values(fields, OID_BASIC_CONSTRAINTS)
    if not found:
        return False
    value = found[0][1]
    _, start, end = der_single(value)
    children = der_children(value, start, end)
    return bool(children) and children[0][0] == 0x01 and der_boolean(value, children[0])


def check_general_name(name):
    if name[0] & 0xC0 != 0x80 or name[0] & 0x1F > 8:
        raise ValueError("a general name is not one of the RFC 5280 alternatives")


def distribution_points(fields):
    """Returns the HTTP points of a certificate's CRL distribution points extension as (normalized locations, names a
    CRL issuer, covers all reasons), and whether it names distribution points at all. The extension is read as
    strictly as the library reads it; a malformed one raises ValueError."""
    found = extension_values(fields, OID_CRL_DISTRIBUTION_POINTS)
    if not found:
        return [], False
    if len(found) > 1:
        raise ValueError("a certificate carries the CRL distribution points extension more than once")
    value = found[0][1]
    tag, start, end = der_single(value)
    if tag != 0x30:
        raise ValueError("malformed CRL distribution points")
    points = []
    for point in der_children(value, start, end):
        if point[0] != 0x30:
            raise ValueError("malformed CRL distribution point")
        parts, index = der_children(value, point[1], point[2]), 0
        locations, named, all_reasons, crl_issuer = [], False, True, False
        if index < len(parts) and parts[index][0] == 0xA0:
            name = der_children(value, parts[index][1], parts[index][2])
            if len(name) != 1 or name[0][0] not in (0xA0, 0xA1):
                raise ValueError("a distribution point name is a full name or a name relative to the CRL issuer")
            if name[0][0] == 0xA0:
                general_names = der_children(value, name[0][1], name[0][2])
                if not general_names:
                    raise ValueError("a full name holds at least one general name")
                for general_name in general_names:
                    check_general_name(general_name)
                    if general_name[0] == 0x86:
                        location = normalize_uri(value[general_name[1]:general_name[2]].decode("ascii"))
                        if location is not None:
                            locations.append(location)
            named, index = True, index + 1
        if index < len(parts) and parts[index][0] == 0x81:
            bits = value[parts[index][1]:parts[index][2]]
            if not bits or bits[0] > 7:
                raise ValueError("malformed reason flags")
            all_reasons, index = covers_all_reasons(bits), index + 1
        if index < len(parts) and parts[index][0] == 0xA2:
            names = der_children(value, parts[index][1], parts[index][2])
            if not names:
                raise ValueError("a CRL issuer holds at least one general name")
            for general_name in names:
                check_general_name(general_name)
            named, crl_issuer, index = True, True, index + 1
        if index != len(parts) or not named:
            raise ValueError("a distribution point names a distribution point or a CRL issuer and nothing else")
        if locations:
            points.append((locations, crl_issuer, all_reasons))
    return points, True


def crl_sources(chain):
    """The CRL sources of a chain's non-root certificates in chain and extension order, each listed once:
    {locations, issuer (None for an indirect CRL issuer), fromCa, fromEndEntity}, the last two telling whether a CA or
    an end-entity certificate names it. Raises InvalidSources when a certificate names
    distribution points, but none with an HTTP location covers all reasons, and ValueError for malformed ones."""
    sources, keys = [], {}
    for certificate in chain[:-1]:
        fields = certificate_fields(certificate)
        points, names_points = distribution_points(fields)
        if names_points and not any(all_reasons for _, _, all_reasons in points):
            raise InvalidSources("a certificate names CRL distribution points, but none with an HTTP or HTTPS location covers all revocation reasons")
        from_ca = is_ca(fields)
        for locations, crl_issuer, _ in points:
            issuer = None if crl_issuer else fields["issuer"]
            key = (" ".join(locations), (issuer or b"").hex())
            if key in keys:
                sources[keys[key]]["fromCa"] |= from_ca
                sources[keys[key]]["fromEndEntity"] |= not from_ca
            else:
                keys[key] = len(sources)
                sources.append({"locations": locations, "issuer": issuer, "fromCa": from_ca, "fromEndEntity": not from_ca})
    return sources


def read_extension(data, extension):
    """Reads an extension as (oid, critical, value bytes): an OID, an optional BOOLEAN and an OCTET STRING."""
    parts = der_children(data, extension[1], extension[2])
    if extension[0] != 0x30 or len(parts) not in (2, 3) or parts[-1][0] != 0x04 or (len(parts) == 3 and parts[1][0] != 0x01):
        raise ValueError("malformed extension")
    critical = len(parts) == 3 and der_boolean(data, parts[1])
    return der_oid(data, parts[0]), critical, data[parts[-1][1]:parts[-1][2]]


def crl_parts(crl):
    """Returns the CRL's TBS fields after the optional version, its encoded TBS signature algorithm, its outer signature
    algorithm and its signature value. A stated version must be 2."""
    _, start, end = der_single(crl)
    outer = der_children(crl, start, end)
    if len(outer) != 3 or outer[0][0] != 0x30 or outer[1][0] != 0x30 or outer[2][0] != 0x03:
        raise ValueError("a CRL has three parts")
    fields = der_children(crl, outer[0][1], outer[0][2])
    offset = 0
    if fields[0][0] == 0x02:
        if der_integer(crl, fields[0]) != 1:
            raise ValueError("a CRL that states its version is a version 2 CRL")
        offset = 1
    return fields[offset:], crl[fields[offset][3]:fields[offset][4]], outer[1], crl[outer[2][1]:outer[2][2]]


def crl_fields(crl):
    """Returns the CRL's encoded issuer, its thisUpdate, its entries as (serial, revoked from, issuers, removed) and its
    CRL extensions as (oid, critical, value). Unknown entry extensions are ignored; of a repeated known one, the last
    applies."""
    fields, _, _, _ = crl_parts(crl)
    if fields[1][0] != 0x30:
        raise ValueError("a CRL issuer is a distinguished name")
    issuer = crl[fields[1][3]:fields[1][4]]
    this_update = der_time(crl, *fields[2][:3])
    rest = fields[3:]
    if rest and rest[0][0] in (0x17, 0x18):
        rest = rest[1:]
    entries, extensions, issuers = [], [], [issuer]
    if rest and rest[0][0] == 0x30:
        for entry in der_children(crl, rest[0][1], rest[0][2]):
            parts = der_children(crl, entry[1], entry[2])
            if entry[0] != 0x30 or len(parts) not in (2, 3) or parts[0][0] != 0x02:
                raise ValueError("malformed CRL entry")
            revocation_date = der_time(crl, *parts[1][:3])
            invalidity_date, removed = None, False
            if len(parts) == 3:
                if parts[2][0] != 0x30:
                    raise ValueError("malformed CRL entry extensions")
                for extension in der_children(crl, parts[2][1], parts[2][2]):
                    oid, _, value = read_extension(crl, extension)
                    if oid == OID_REASON_CODE:
                        removed = der_only(value, 0x0A) == b"\x08"
                    elif oid == OID_INVALIDITY_DATE:
                        tag, start, end = der_single(value)
                        if tag != 0x18:
                            raise ValueError("an invalidity date is a GeneralizedTime")
                        invalidity_date = der_time(value, tag, start, end)
                    elif oid == OID_CERTIFICATE_ISSUER:
                        # Applies to this and every following entry; without a directoryName the issuer is unknown.
                        _, start, end = der_single(value)
                        issuers = []
                        for name in der_children(value, start, end):
                            if name[0] == 0xA4:
                                directory_name = der_children(value, name[1], name[2])
                                if len(directory_name) != 1:
                                    raise ValueError("a directory name holds one name")
                                issuers.append(value[directory_name[0][3]:directory_name[0][4]])
            revoked_from = invalidity_date if invalidity_date is not None and invalidity_date < revocation_date else revocation_date
            entries.append((crl[parts[0][1]:parts[0][2]], revoked_from, list(issuers), removed))
        rest = rest[1:]
    if rest:
        if len(rest) != 1 or rest[0][0] != 0xA0:
            raise ValueError("malformed CRL extensions")
        wrapped = der_children(crl, rest[0][1], rest[0][2])
        if len(wrapped) != 1 or wrapped[0][0] != 0x30:
            raise ValueError("malformed CRL extensions")
        extensions = [read_extension(crl, extension) for extension in der_children(crl, wrapped[0][1], wrapped[0][2])]
    return issuer, this_update, entries, extensions


def issuing_point_covers(value, locations, from_ca, from_end_entity):
    """Whether an issuing distribution point leaves a CRL complete for a source: its distribution point, if any, is a
    full name with one of the locations, onlyContainsUserCerts is not set for a source a CA certificate names,
    onlyContainsCACerts not for one an end-entity certificate names, and none of onlySomeReasons, indirectCRL and
    onlyContainsAttributeCerts is set."""
    tag, start, end = der_single(value)
    if tag != 0x30:
        raise ValueError("malformed issuing distribution point")
    parts, index, names_location = der_children(value, start, end), 0, True
    if index < len(parts) and parts[index][0] == 0xA0:
        name = der_children(value, parts[index][1], parts[index][2])
        if len(name) != 1:
            raise ValueError("malformed distribution point name")
        names_location = name[0][0] == 0xA0 and any(
            general_name[0] == 0x86 and normalize_uri(value[general_name[1]:general_name[2]].decode("ascii")) in locations
            for general_name in der_children(value, name[0][1], name[0][2]))
        index += 1

    def flag(tag):
        nonlocal index
        if index < len(parts) and parts[index][0] == tag:
            index += 1
            return der_boolean(value, parts[index - 1])
        return False

    only_user_certificates = flag(0x81)
    only_ca_certificates = flag(0x82)
    restricted = (only_user_certificates and from_ca) or (only_ca_certificates and from_end_entity)
    if index < len(parts) and parts[index][0] == 0x83:
        restricted, index = True, index + 1
    restricted |= flag(0x84)
    restricted |= flag(0x85)
    if index != len(parts):
        raise ValueError("malformed issuing distribution point")
    return names_location and not restricted


def crl_complete_for(extensions, source):
    """A CRL is complete for a source when it is no delta CRL, repeats no extension, carries no critical extension
    besides the issuing distribution point and the CRL number, and an issuing distribution point covers the source."""
    seen = set()
    for oid, critical, value in extensions:
        if oid in seen or oid == OID_DELTA_CRL_INDICATOR:
            return False
        seen.add(oid)
        if oid == OID_ISSUING_DISTRIBUTION_POINT:
            if not issuing_point_covers(value, source["locations"], source["fromCa"], source["fromEndEntity"]):
                return False
        elif oid != OID_CRL_NUMBER and critical:
            return False
    return True


def hash_algorithm(data, node):
    """The SHA-2 hash an AlgorithmIdentifier names with NULL or absent parameters, or None."""
    parts = der_children(data, node[1], node[2])
    if node[0] != 0x30 or not parts or len(parts) > 2 or (len(parts) == 2 and data[parts[1][3]:parts[1][4]] != DER_NULL):
        raise ValueError("malformed hash algorithm")
    oid = der_oid(data, parts[0])
    return oid if oid in SHA2_DIGESTS else None


def pss_hash(crl, parameters):
    """The hash of RSASSA-PSS parameters in the form the library verifies: an explicit SHA-2 hash, MGF1 with the same
    hash, a salt as long as the hash and the default trailer, in this order; None otherwise."""
    if parameters[0] != 0x30:
        return None
    fields = der_children(crl, parameters[1], parameters[2])
    if len(fields) < 3 or [field[0] for field in fields[:3]] != [0xA0, 0xA1, 0xA2] or (len(fields) == 4 and fields[3][0] != 0xA3) or len(fields) > 4:
        return None
    inner = [der_children(crl, field[1], field[2]) for field in fields]
    if any(len(children) != 1 for children in inner):
        return None
    digest = hash_algorithm(crl, inner[0][0])
    mask = der_children(crl, inner[1][0][1], inner[1][0][2])
    if digest is None or inner[1][0][0] != 0x30 or len(mask) != 2 or der_oid(crl, mask[0]) != OID_MGF1 or hash_algorithm(crl, mask[1]) != digest:
        return None
    if der_integer(crl, inner[2][0]) != SHA2_DIGESTS[digest] or (len(fields) == 4 and der_integer(crl, inner[3][0]) != 1):
        return None
    return digest


def key_size(public_key):
    """The algorithm and size of a DER SubjectPublicKeyInfo: RSA keys by their modulus, ECDSA keys by their curve, or by
    their point for an unknown curve; the size is None for other key types."""
    _, start, end = der_single(public_key)
    algorithm, key = der_children(public_key, start, end)
    algorithm_parts = der_children(public_key, algorithm[1], algorithm[2])
    oid = der_oid(public_key, algorithm_parts[0])
    bits = public_key[key[1] + 1:key[2]]
    if oid in (OID_RSA, OID_RSASSA_PSS):
        _, rsa_start, rsa_end = der_single(bits)
        modulus = der_children(bits, rsa_start, rsa_end)[0]
        return oid, int.from_bytes(bits[modulus[1]:modulus[2]], "big").bit_length()
    if oid == OID_EC_PUBLIC_KEY:
        curve = der_oid(public_key, algorithm_parts[1]) if len(algorithm_parts) > 1 and algorithm_parts[1][0] == 0x06 else None
        return oid, CURVE_SIZES.get(curve, (len(bits) - 1) // 2 * 8 if bits[:1] == b"\x04" else len(bits) * 8)
    return oid, None


def permits_crl_signing(fields):
    """Whether a certificate carries no key usage extension or one that permits cRLSign."""
    try:
        usage = key_usage_bits(fields)
    except PARSE_ERRORS:
        return False
    return usage is None or bool(usage[0] & 0x02)


def crl_signature_accepted(crl, issuer_certificate, issuer_fields, directory):
    """Applies the CRL signature policy: the TBS and outer algorithms are equal, the algorithm is RSA PKCS #1 v1.5,
    RSASSA-PSS or ECDSA with SHA-256, -384 or -512 and its parameters have the form the library accepts, the issuer's key
    is an RSA or ECDSA key of the required size, and OpenSSL verifies the signature."""
    _, tbs_algorithm, outer_algorithm, signature = crl_parts(crl)
    if tbs_algorithm != crl[outer_algorithm[3]:outer_algorithm[4]] or signature[:1] != b"\x00":
        return False
    parts = der_children(crl, outer_algorithm[1], outer_algorithm[2])
    oid = der_oid(crl, parts[0])
    parameters = [crl[part[3]:part[4]] for part in parts[1:]]
    if oid in RSA_CRL_SIGNATURES:
        accepted = parameters in ([], [DER_NULL])
    elif oid in ECDSA_CRL_SIGNATURES:
        accepted = not parameters
    else:
        accepted = oid == OID_RSASSA_PSS and len(parts) == 2 and pss_hash(crl, parts[1]) is not None
    algorithm, size = key_size(issuer_fields["publicKey"])
    if not accepted or algorithm not in MINIMUM_KEY_SIZES or size is None or size < MINIMUM_KEY_SIZES[algorithm]:
        return False
    crl_path, issuer_path = os.path.join(directory, "crl.der"), os.path.join(directory, "issuer.pem")
    with open(crl_path, "wb") as stream:
        stream.write(crl)
    with open(issuer_path, "w") as stream:
        stream.write(pem(issuer_certificate))
    code, output = openssl("crl", "-inform", "DER", "-in", crl_path, "-CAfile", issuer_path, "-noout")
    return code == 0 and "verify OK" in output


def token_info(token):
    """Decodes an RFC 3161 token (CMS SignedData over a TSTInfo) and returns its policy OID, genTime, message imprint,
    signer identifier, signer digest and signature algorithms, and whether it carries a countersignature. Raises
    ValueError when the signed content is not exactly one TSTInfo, there is not exactly one signer, or the token is not
    exactly one DER value."""
    _, start, end = der_single(token)
    content_info = der_children(token, start, end)
    signed_data = der_children(token, *der_children(token, content_info[1][1], content_info[1][2])[0][1:3])
    encapsulated = der_children(token, signed_data[2][1], signed_data[2][2])
    if der_oid(token, encapsulated[0]) != OID_ID_CT_TSTINFO:
        raise ValueError("the signed content is not a TSTInfo")
    octets = der_children(token, encapsulated[1][1], encapsulated[1][2])[0]
    if octets[0] != 0x04:
        raise ValueError("the TSTInfo is not in an OCTET STRING")
    tst_info = token[octets[1]:octets[2]]
    _, start, end = der_single(tst_info)
    fields = der_children(tst_info, start, end)
    imprint = der_children(tst_info, fields[2][1], fields[2][2])
    imprint_algorithm = der_children(tst_info, imprint[0][1], imprint[0][2])
    signer_infos = der_children(token, signed_data[-1][1], signed_data[-1][2])
    if signed_data[-1][0] != 0x31 or len(signer_infos) != 1:
        raise ValueError("the token does not have exactly one signer")
    signer = der_children(token, signer_infos[0][1], signer_infos[0][2])
    if signer[1][0] == 0x30:
        sid_parts = der_children(token, signer[1][1], signer[1][2])
        sid = ("issuerAndSerialNumber", token[sid_parts[0][3]:sid_parts[0][4]], token[sid_parts[1][1]:sid_parts[1][2]])
    elif signer[1][0] == 0x80:
        sid = ("subjectKeyIdentifier", token[signer[1][1]:signer[1][2]])
    else:
        raise ValueError("unknown signer identifier")
    digest_algorithm = der_oid(token, der_children(token, signer[2][1], signer[2][2])[0])
    rest = signer[4:] if signer[3][0] == 0xA0 else signer[3:]
    signature_algorithm = der_oid(token, der_children(token, rest[0][1], rest[0][2])[0])
    countersigned = False
    for unsigned in (part for part in rest[2:] if part[0] == 0xA1):
        for attribute in der_children(token, unsigned[1], unsigned[2]):
            countersigned |= der_oid(token, der_children(token, attribute[1], attribute[2])[0]) == OID_COUNTERSIGNATURE
    return {
        "tstInfo": tst_info,
        "policy": der_oid(tst_info, fields[1]),
        "imprintAlgorithm": der_oid(tst_info, imprint_algorithm[0]),
        "imprintParameters": [tst_info[part[3]:part[4]] for part in imprint_algorithm[1:]],
        "imprint": der_only(tst_info[imprint[1][3]:imprint[1][4]], 0x04),
        "time": der_time(tst_info, *fields[4][:3]),
        "signer": sid,
        "digestAlgorithm": digest_algorithm,
        "signatureAlgorithm": signature_algorithm,
        "countersigned": countersigned,
    }


def signer_failure(fields):
    """Applies the library's rules for the signing certificate: no extension twice, a critical extended key usage of
    only id-kp-timeStamping, and a key usage, if present, of digitalSignature or nonRepudiation and nothing else."""
    oids = [oid for oid, _, _ in fields["extensions"]]
    duplicate = next((oid for oid in oids if oids.count(oid) > 1), None)
    if duplicate is not None:
        return f"the signing certificate carries the extension {duplicate} more than once"
    purposes = extension_values(fields, OID_EXTENDED_KEY_USAGE)
    names = []
    if purposes:
        value = purposes[0][1]
        _, start, end = der_single(value)
        names = [der_oid(value, purpose) for purpose in der_children(value, start, end)]
    if not names or any(name != OID_TIME_STAMPING for name in names):
        return "the signing certificate's extended key usage must contain only id-kp-timeStamping"
    if not purposes[0][0]:
        return "the signing certificate's extended key usage extension must be critical"
    usage = key_usage_bits(fields)
    if usage is not None and not usage[0] & 0xC0:
        return "the signing certificate's key usage permits neither digitalSignature nor nonRepudiation"
    if usage is not None and (usage[0] & 0x3F or usage[1] & 0x80):
        return "the signing certificate's key usage permits more than digitalSignature and nonRepudiation"
    return None


# The string types a name's attribute value is decoded from, by their DER tags.
NAME_STRING_CODECS = {0x0C: "utf-8", 0x13: "ascii", 0x14: "latin-1", 0x16: "ascii", 0x1E: "utf-16-be"}
ASCII_LOWER = str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz")


def name_key(name):
    """The comparable form of a DER-encoded name: its relative distinguished names in order, each a list of its
    attribute types in order with their values. A value of a string type is decoded and its ASCII letters lower-cased, so
    names that differ only in string type or ASCII case are equal; any other value is kept as its encoding."""
    tag, start, end = der_single(name)
    if tag != 0x30:
        raise ValueError("expected a name")
    key = []
    for rdn in der_children(name, start, end):
        if rdn[0] != 0x31:
            raise ValueError("expected a relative distinguished name")
        attributes = []
        for attribute in der_children(name, rdn[1], rdn[2]):
            if attribute[0] != 0x30:
                raise ValueError("expected an attribute")
            parts = der_children(name, attribute[1], attribute[2])
            if len(parts) != 2:
                raise ValueError("expected an attribute type and value")
            codec = NAME_STRING_CODECS.get(parts[1][0])
            value = name[parts[1][3]:parts[1][4]] if codec is None else name[parts[1][1]:parts[1][2]].decode(codec).translate(ASCII_LOWER)
            attributes.append((der_oid(name, parts[0]), value))
        key.append(attributes)
    return key


def chain_failure(chain_fields, chain, signer, trusted_roots):
    """Checks the stored chain's structure: it starts with the token's signer, each certificate names the next one's
    subject as its issuer, and it ends with a self-issued trusted root. Issuer and subject names are compared in the form
    name_key gives them."""
    first_fields = chain_fields[0]
    if signer[0] == "issuerAndSerialNumber":
        is_signer = signer[1] == first_fields["issuer"] and signer[2] == first_fields["serial"]
    else:
        identifiers = extension_values(first_fields, OID_SUBJECT_KEY_IDENTIFIER)
        is_signer = bool(identifiers) and der_only(identifiers[0][1], 0x04) == signer[1]
    if not is_signer:
        return "the stored chain does not start with the token's signing certificate"
    if any(name_key(chain_fields[index]["issuer"]) != name_key(chain_fields[index + 1]["subject"]) for index in range(len(chain) - 1)):
        return "the stored chain does not link each certificate to its issuer"
    if name_key(chain_fields[-1]["issuer"]) != name_key(chain_fields[-1]["subject"]) or chain[-1] not in trusted_roots:
        return "the stored chain does not end with a trusted root"
    return None


def pem(der, label="CERTIFICATE"):
    body = base64.encodebytes(der).decode("ascii").replace("\n", "")
    lines = [body[i:i + 64] for i in range(0, len(body), 64)]
    return f"-----BEGIN {label}-----\n" + "\n".join(lines) + f"\n-----END {label}-----\n"


def pem_certificates(text):
    """The DER certificates in PEM text."""
    return [base64.b64decode(block) for block in re.findall(r"-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----", text, re.S)]


def openssl(*arguments):
    try:
        result = subprocess.run(["openssl", *arguments], capture_output=True, text=True, timeout=OPENSSL_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        return -1, f"openssl did not finish within {OPENSSL_TIMEOUT_SECONDS} seconds"
    return result.returncode, result.stdout + result.stderr


class Trust:
    """Trusted roots and required policies per authority; roots without a name apply to every authority."""

    def __init__(self, roots=None, policies=None):
        self.roots = roots or {}
        self.policies = policies or {}

    def roots_for(self, authority):
        return self.roots.get(None, []) + self.roots.get(authority, [])


class Revocation:
    """Evaluates CRLs for the CRL sources of a chain by one rule for the revocation status and the consistency of the
    stored status: a CRL counts for a source when the source names its issuer, the CRL comes from that issuer and was
    issued at or after a time, it is complete for the source, and its signature verifies against the issuer's
    certificate in the chain, which permits cRLSign. CRLs are matched with sources by content, in any order."""

    def __init__(self, chain, sources, lists, directory):
        self.chain, self.sources, self.directory = chain, sources, directory
        self.chain_fields = [certificate_fields(certificate) for certificate in chain]
        self.stored_lists = len(lists)
        self.lists, self.signatures = [], {}
        for crl in lists:
            try:
                self.lists.append((crl, crl_fields(crl)))
            except PARSE_ERRORS:
                continue

    def evaluate(self, issued_not_before):
        """For each source whether a CRL counts for it, and the entries of every CRL that counts for a source."""
        covered, entries = [False] * len(self.sources), []
        for crl, (issuer, this_update, crl_entries, extensions) in self.lists:
            counts = False
            for index, source in enumerate(self.sources):
                if self.counts_for(crl, issuer, this_update, extensions, source, issued_not_before):
                    covered[index] = counts = True
            if counts:
                entries += crl_entries
        return covered, entries

    def counts_for(self, crl, issuer, this_update, extensions, source, issued_not_before):
        if source["issuer"] is None or issuer != source["issuer"] or this_update < issued_not_before:
            return False
        try:
            if not crl_complete_for(extensions, source):
                return False
            index = next((index for index, fields in enumerate(self.chain_fields) if fields["subject"] == issuer), None)
            if index is None or not permits_crl_signing(self.chain_fields[index]):
                return False
            key = (crl, index)
            if key not in self.signatures:
                self.signatures[key] = crl_signature_accepted(crl, self.chain[index], self.chain_fields[index], self.directory)
            return self.signatures[key]
        except PARSE_ERRORS:
            return False

    def revoked(self, entries, token_time):
        """Whether an entry revokes a non-root certificate of the chain at or before the token's time."""
        return any(not removed and serial == fields["serial"] and (not issuers or fields["issuer"] in issuers) and revoked_from <= token_time
                   for fields in self.chain_fields[:-1] for serial, revoked_from, issuers, removed in entries)

    def status(self, token_time, grace):
        """Revoked when a CRL issued after the token's time revokes a certificate of the chain at or before it, whatever
        the grace period: it only guards the conclusion that nothing is revoked. Confirmed when CRLs issued at least the
        grace period after the token's time count for every source, or there are no sources and no stored CRL."""
        _, entries = self.evaluate(token_time + datetime.timedelta(microseconds=1))
        if self.revoked(entries, token_time):
            return "Revoked"
        covered, _ = self.evaluate(later(token_time, grace))
        confirmed = all(covered) if self.sources else self.stored_lists == 0
        return "Confirmed" if confirmed else "Unconfirmed"

    def inconsistency(self, stored_status, token_time):
        """Why a stored status of Confirmed or Revoked lacks the CRLs it was recorded from, or None. It applies to a
        token whose status is not Revoked: a stored Revoked then lacks its revoking CRL, and a stored Confirmed lacks
        a CRL issued after the token's time for a source that names its issuer. The grace period plays no role."""
        if stored_status == "Revoked":
            return "the stored status is revoked, but no stored CRL issued after the token's time revokes a certificate of its chain"
        if stored_status != "Confirmed":
            return None
        covered, _ = self.evaluate(token_time + datetime.timedelta(microseconds=1))
        if any(source["issuer"] is not None and not covered[index] for index, source in enumerate(self.sources)):
            return "the stored status is confirmed, but confirming CRLs are missing for a distribution point"
        return None


def verify_timestamp(entry, evidence, digest, trust, grace):
    """Verifies one stored token: its structure, imprint, signer, algorithms and stored chain itself, its signature and
    chain with OpenSSL, and recomputes its revocation status from the confirming CRLs."""
    report = {"authority": entry["Authority"], "valid": False, "failure": None, "revocationStatus": "Unconfirmed", "storedRevocationStatus": entry["RevocationStatus"]}

    def invalid(failure):
        report["failure"] = failure
        return report

    roots = trust.roots_for(entry["Authority"])
    if not roots:
        return invalid(f"no trust settings are configured for timestamp authority '{entry['Authority']}'")
    try:
        token = evidence.resolve(entry["TokenHash"])
        chain = [evidence.resolve(item) for item in entry["CertificateChainHashes"]]
        for item in entry["CertificateRevocationListHashes"]:
            evidence.resolve(item)
        confirming = [evidence.resolve(item) for item in entry["ConfirmingRevocationListHashes"]]
    except LookupError as error:
        return invalid(str(error))
    try:
        info = token_info(token)
    except PARSE_ERRORS:
        return invalid("malformed token: the signed content is not exactly one TSTInfo with one signer")
    try:
        require_der(token)
        require_der(info["tstInfo"])
    except PARSE_ERRORS:
        return invalid("the token is not DER-encoded")
    if info["imprintAlgorithm"] != OID_SHA256 or info["imprintParameters"] not in ([], [DER_NULL]) or info["imprint"] != digest:
        return invalid("the token's imprint is not SHA-256 over the anchor digest")
    token_time, stored_time = info["time"], datetime_ms(entry["TimestampUtc"])
    if token_time < stored_time or token_time - stored_time >= datetime.timedelta(milliseconds=1):
        return invalid("the token's time differs from the stored time")
    if info["countersigned"]:
        return invalid("the token carries a countersignature besides the authority's signature")
    policy = trust.policies.get(entry["Authority"])
    if policy is not None and info["policy"] != policy:
        return invalid(f"the token is issued under policy {info['policy']}, not the required policy {policy}")
    if not chain:
        return invalid("the stored certificate chain is empty")
    trusted_roots = []
    for root in roots:
        with open(root) as source:
            trusted_roots.append(source.read())
    try:
        chain_fields = [certificate_fields(certificate) for certificate in chain]
        failure = signer_failure(chain_fields[0])
        if failure is None and (info["digestAlgorithm"] in WEAK_DIGESTS or info["signatureAlgorithm"] in WEAK_SIGNATURES):
            failure = "the token is signed with a weak algorithm; MD5 and SHA-1 are not accepted"
        algorithm, size = key_size(chain_fields[0]["publicKey"])
        minimum = MINIMUM_KEY_SIZES.get(algorithm, 0)
        if failure is None and ((size is None and minimum > 0) or (size is not None and size < minimum)):
            failure = "the signing key is not of the required size"
        failure = failure or chain_failure(chain_fields, chain, info["signer"], [der for text in trusted_roots for der in pem_certificates(text)])
    except PARSE_ERRORS:
        failure = "a certificate of the stored chain is malformed"
    if failure:
        return invalid(failure)
    try:
        sources = crl_sources(chain)
    except InvalidSources as error:
        return invalid(str(error))
    except PARSE_ERRORS:
        return invalid("a certificate of the stored chain has a malformed CRL distribution point")
    with tempfile.TemporaryDirectory() as directory:
        token_path, chain_path, roots_path = (os.path.join(directory, name) for name in ("token.der", "chain.pem", "roots.pem"))
        with open(token_path, "wb") as stream:
            stream.write(token)
        with open(chain_path, "w") as stream:
            stream.write("".join(pem(certificate) for certificate in chain))
        with open(roots_path, "w") as stream:
            stream.write("".join(trusted_roots))
        code, output = openssl("ts", "-verify", "-digest", digest.hex(), "-in", token_path, "-token_in",
                               "-CAfile", roots_path, "-untrusted", chain_path, "-attime", str(int(token_time.timestamp())))
        if code != 0 or "Verification: OK" not in output:
            return invalid("openssl ts -verify failed: " + output.strip().splitlines()[-1] if output.strip() else "openssl ts -verify failed")
        revocation = Revocation(chain, sources, confirming, directory)
        report["revocationStatus"] = revocation.status(token_time, grace)
        if report["revocationStatus"] == "Revoked":
            return invalid("a certificate of the token's chain is revoked at or before the token's time")
        inconsistency = revocation.inconsistency(entry["RevocationStatus"], token_time)
        if inconsistency is not None:
            report["revocationStatus"] = "Unconfirmed"
            return invalid(f"the stored revocation evidence is inconsistent: {inconsistency}")
    report["valid"] = True
    return report


def verify_anchor(anchor, evidence, trust, grace):
    digest_valid = anchor_digest(anchor["FormatVersion"], anchor["Sequence"], anchor["PreviousDigest"], anchor["MerkleRoot"]) == anchor["AnchorDigest"]
    timestamps = [verify_timestamp(entry, evidence, anchor["AnchorDigest"], trust, grace) for entry in anchor["Timestamps"]]
    return {"sequence": anchor["Sequence"], "digestValid": digest_valid, "timestamps": timestamps,
            "verified": digest_valid and any(timestamp["valid"] for timestamp in timestamps)}


# --- Commands -------------------------------------------------------------------------------------------------------


def stream_documents(documents, aggregate_id, malformed=None):
    """The documents of one stream, as the library's query selects them, including a document whose AggregateId array
    holds the identifier. Documents that cannot be read are skipped and listed in malformed."""
    selected = []
    for index, document in enumerate(documents):
        try:
            if selects_uuid(document, "AggregateId", aggregate_id):
                selected.append(document)
        except PARSE_ERRORS as error:
            if malformed is not None:
                malformed.append({"collection": "events", "index": index, "error": str(error)})
    return selected


def stream_leaf_version(document, aggregate_type, aggregate_id):
    """The version of a leaf document that the library counts among a stream's leaves, as its query selects them: the
    stream's aggregate type and identifier and a numeric Version of at least 1, each also matched by an array that
    holds such a value. An integer version is returned as stored, any other as 1; None for leaves of other streams."""
    try:
        found = first_raw(document, "Version")
        if (not any(sort_key(value)[:2] == (2, 1) and sort_key(value)[2] >= 1 for value in query_values(found) if value[0] != T_ARRAY) or
                not selects_string(document, "AggregateType", aggregate_type) or not selects_uuid(document, "AggregateId", aggregate_id)):
            return None
        return max(1, decode(*found)) if found[0] in (T_INT32, T_INT64) else 1
    except PARSE_ERRORS:
        return None


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
    anchors, unreadable_anchors = {}, set()
    for document in anchor_documents:
        anchor = read_anchor(document)
        if anchor is not None:
            anchors[anchor["Id"]] = anchor
            continue
        try:
            found = first_raw(document, "_id")
            if first_raw(document, "Sequence") is not None and found is not None and found[0] == T_BINARY and found[1][4] == SUBTYPE_UUID:
                unreadable_anchors.add(found[1][5:])
        except PARSE_ERRORS:
            continue
    evidence = Evidence(read_documents(arguments.evidence), read_documents(arguments.evidence_chunks) if arguments.evidence_chunks else [])
    malformed += evidence.malformed
    leaves, first_unreadable = [], None
    for index, document in enumerate(leaf_documents):
        leaf = read_leaf(document)
        if leaf is None:
            malformed.append({"collection": "leaves", "index": index, "error": "the leaf cannot be read"})
        claimed = stream_leaf_version(document, arguments.aggregate_type, aggregate_id)
        if claimed is None:
            continue
        # A stream leaf that cannot be read, or whose committed anchor cannot be read, breaks the stream at its version.
        if leaf is None or leaf["AnchorId"] in unreadable_anchors:
            first_unreadable = claimed if first_unreadable is None else min(first_unreadable, claimed)
            continue
        anchor = anchors.get(leaf["AnchorId"])
        if anchor is not None and anchor["Status"] == "Completed" and anchor["Sequence"] == leaf["AnchorSequence"]:
            leaves.append((anchor, leaf))
    leaves.sort(key=lambda item: item[0]["Sequence"])
    report["anchorChain"] = verify_anchor_chain(anchor_documents, leaf_documents, trusted_head)
    report["stream"] = verify_stream(events, aggregate_id, [(leaf["Version"], leaf["Hash"], proves_inclusion(leaf, anchor)) for anchor, leaf in leaves],
                                     first_unreadable)
    ok = report["stream"]["intact"] and report["anchorChain"]["intact"]
    for anchor, leaf in leaves:
        anchor_report = verify_anchor(anchor, evidence, trust, arguments.grace)
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
    check_parser.add_argument("--grace-hours", type=float, help="the hours after a token's time from which confirming CRLs count; 24 by default")
    check_parser.add_argument("--trusted-head", help="SEQUENCE:DIGESTHEX of an anchor kept outside the database")
    arguments = parser.parse_args()
    try:
        if arguments.command == "vectors":
            report, ok = vectors(arguments.directory)
        else:
            if arguments.anchors and not (arguments.leaves and arguments.evidence and arguments.trusted_root):
                parser.error("--anchors needs --leaves, --evidence and at least one --trusted-root")
            anchor_options = (arguments.leaves, arguments.evidence, arguments.evidence_chunks, arguments.trusted_root, arguments.policy,
                              arguments.trusted_head, arguments.grace_hours)
            if not arguments.anchors and any(option is not None and option != [] for option in anchor_options):
                parser.error("--leaves, --evidence, --evidence-chunks, --trusted-root, --policy, --trusted-head and --grace-hours need --anchors")
            try:
                arguments.grace = datetime.timedelta(hours=24 if arguments.grace_hours is None else arguments.grace_hours)
            except (OverflowError, ValueError):
                arguments.grace = None
            if arguments.grace is None or arguments.grace <= datetime.timedelta(0):
                parser.error("--grace-hours expects a positive number of hours")
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
