"""Rowing Mongo helpers shared by the crawlers that write the Rowing staging shape
(`<DS>_Inventories_NEW`, one document per listing): paciolanevenue and axs.

- build_listing_id: port of ETECH ListingIdentity.Build (the document `_id`).
- get_client: one pooled MongoClient per (uri, db).
"""

import threading
from typing import Optional

# --- ListingIdentity.cs, ported 1:1 (unchecked ulong djb2-variant hash +
# base-36 compact encode) - see that file's comment for why the hash
# preimage includes the event id, and VerifyKnownVectors for its own
# self-test (not ported here; this implementation follows the same
# published algorithm, byte for byte, but was not cross-checked against
# the C# runtime output since nothing here can execute C#). -------------

_MASK64 = (1 << 64) - 1
_COMPACT_ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
_COMPACT_RADIX = 36
_COMPACT_LENGTH = 13


def get_deterministic_hash_code(s: Optional[str]) -> int:
    if s is None:
        s = ""
    hash1 = ((5381 << 16) + 5381) & _MASK64
    hash2 = hash1
    n = len(s)
    i = 0
    while i < n:
        hash1 = (((hash1 << 5) + hash1) ^ ord(s[i])) & _MASK64
        if i == n - 1:
            break
        hash2 = (((hash2 << 5) + hash2) ^ ord(s[i + 1])) & _MASK64
        i += 2
    return (hash1 + hash2 * 1566083941) & _MASK64


def compact_encode(hash_val: int) -> str:
    chars = [""] * _COMPACT_LENGTH
    h = hash_val
    for i in range(_COMPACT_LENGTH - 1, -1, -1):
        chars[i] = _COMPACT_ALPHABET[h % _COMPACT_RADIX]
        h //= _COMPACT_RADIX
    return "".join(chars)


def build_listing_id(event_id: str, fingerprint: str) -> str:
    """Port of ListingIdentity.Build(eventId, fingerprint)."""
    preimage = (event_id or "") + "\n" + (fingerprint or "")
    return compact_encode(get_deterministic_hash_code(preimage))


_clients_lock = threading.Lock()
_clients: dict = {}  # (mongo_uri, mongo_db) -> pymongo.MongoClient


def get_client(mongo_uri: str, mongo_db: str):
    """Lazily connects once per (uri, db) and reuses the same pooled client
    across calls - same pattern as broadwaydirect/api.py's `_get_mongo()`
    singleton, instead of opening (and, on any failure, leaking) a brand new
    MongoClient - with its own connection pool and background monitor
    thread - on every single write_inventory() call. A failed connect isn't
    cached, so the next call retries cleanly."""
    import pymongo

    key = (mongo_uri, mongo_db)
    with _clients_lock:
        client = _clients.get(key)
        if client is not None:
            return client
        client = pymongo.MongoClient(mongo_uri, serverSelectionTimeoutMS=5000)
        try:
            client.admin.command("ping")
        except Exception:
            client.close()
            raise
        _clients[key] = client
        return client
