"""Decode a local Stella server list; provide the matching key with STELLA_META_KEY or --key."""
from __future__ import annotations

import argparse
import os
import json
import sys
import urllib.request

from Crypto.Cipher import AES

DEFAULT_URL = "https://nova-static.yostar.cn/meta/serverlist.html"
DEFAULT_KEY = os.environ.get("STELLA_META_KEY", "").encode("utf-8")


def rd_varint(b, i):
    r = 0
    s = 0
    while True:
        x = b[i]
        i += 1
        r |= (x & 0x7F) << s
        if not x & 0x80:
            return r, i
        s += 7


def parse_msg(b, schema):
    """schema: {field_no: (name, type)} ; type in 'varint','string','msg','bool','bytes'"""
    i = 0
    out = {}
    n = len(b)
    while i < n:
        key, i = rd_varint(b, i)
        fno, wt = key >> 3, key & 7
        ent = schema.get(fno)
        name = ent[0] if ent else f"field{fno}"
        typ = ent[1] if ent else ("bytes" if wt == 2 else "varint")
        if wt == 0:
            v, i = rd_varint(b, i)
            out[name] = bool(v) if typ == "bool" else v
        elif wt == 2:
            ln, i = rd_varint(b, i)
            raw = b[i:i + ln]
            i += ln
            if typ == "string":
                out[name] = raw.decode("utf-8", "replace")
            elif typ == "msg":
                out.setdefault(name, []).append(parse_msg(raw, ent[2]))
            else:
                out[name] = raw.hex()
        elif wt == 5:
            out[name] = int.from_bytes(b[i:i + 4], "little")
            i += 4
        elif wt == 1:
            out[name] = int.from_bytes(b[i:i + 8], "little")
            i += 8
        else:
            break
    return out


RULE_SCHEMA = {
    1: ("Platform", "varint"), 2: ("Channel", "string"), 3: ("Version", "string"),
    4: ("Op", "varint"), 5: ("Action", "varint"), 6: ("Url", "string"), 7: ("Enable", "bool"),
}
AGENT_SCHEMA = {
    1: ("Name", "string"), 2: ("Addr", "string"), 3: ("Status", "varint"), 4: ("Zone", "varint"),
}
META_SCHEMA = {
    1: ("Version", "varint"),
    2: ("Agent", "msg", AGENT_SCHEMA),
    3: ("Status", "varint"),
    4: ("Message", "string"),
    6: ("ReportEndpoint", "string"),
    7: ("Rules", "msg", RULE_SCHEMA),
}


def decrypt(blob: bytes, key: bytes) -> bytes:
    iv, ct = blob[:16], blob[16:]
    pt = AES.new(key, AES.MODE_CBC, iv).decrypt(ct)
    pad = pt[-1]
    if not (1 <= pad <= 16 and pt[-pad:] == bytes([pad]) * pad):
        raise ValueError("padding 不对：key/IV 不匹配")
    return pt[:-pad]


def fetch(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "okhttp/4.9.2"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default=DEFAULT_URL)
    ap.add_argument("--key", default=None)
    ap.add_argument("--raw")
    a = ap.parse_args()
    key = a.key.encode() if a.key else DEFAULT_KEY
    if len(key) != 16:
        ap.error("Provide a 16-byte STELLA_META_KEY or --key")
    blob = open(a.raw, "rb").read() if a.raw else fetch(a.url)
    print(f"[i] ciphertext {len(blob)} bytes")
    pt = decrypt(blob, key)
    print(f"[i] plaintext {len(pt)} bytes (protobuf ServerListMeta)")
    msg = parse_msg(pt, META_SCHEMA)
    print(json.dumps(msg, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
