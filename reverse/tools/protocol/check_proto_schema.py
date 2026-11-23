#!/usr/bin/env python3
"""Verify that every proto.* type referenced by NetMsgId.MsgName exists in proto.pb.

Parses a protobuf FileDescriptorSet without requiring the protobuf runtime.
Only field 1 (file) -> field 4 (message_type) -> field 1 (name) and field 2
(package) are decoded.
"""
import re
import sys
from pathlib import Path

OUTPUT = Path(__file__).resolve().parents[2] / "output"
PB = OUTPUT / "lua_bytecode/_data/1a70da105b39911e.pb"
NETMSGID = OUTPUT / "lua_source/GameCore/Network/NetMsgId.lua"


def varint(d, p):
    v = 0
    shift = 0
    while p < len(d):
        b = d[p]
        p += 1
        v |= (b & 0x7F) << shift
        if not b & 0x80:
            return v, p
        shift += 7
    return v, p


def fields(d):
    """Yield (field_number, wire_type, value) for a protobuf message."""
    p = 0
    while p < len(d):
        key, p = varint(d, p)
        num, wt = key >> 3, key & 7
        if wt == 0:
            v, p = varint(d, p)
            yield num, wt, v
        elif wt == 2:
            ln, p = varint(d, p)
            yield num, wt, d[p:p + ln]
            p += ln
        elif wt == 5:
            yield num, wt, d[p:p + 4]
            p += 4
        elif wt == 1:
            yield num, wt, d[p:p + 8]
            p += 8
        else:
            raise ValueError("wire type %d" % wt)


def main():
    d = open(PB, "rb").read()
    messages = set()
    files = []
    for num, wt, val in fields(d):
        if num != 1:  # FileDescriptorProto
            continue
        pkg = ""
        names = []
        for fn, fw, fv in fields(val):
            if fn == 2:  # package
                pkg = fv.decode("utf-8", "replace")
            elif fn == 4:  # message_type (DescriptorProto), not nested
                for mn, mw, mv in fields(fv):
                    if mn == 1:
                        names.append(mv.decode("utf-8", "replace"))
                        break
        files.append(pkg)
        if pkg:
            for n in names:
                messages.add(pkg + "." + n)
            messages.update(n for n in names)  # also match bare names
    print("proto files:", len(files), "unique packages:", sorted(set(files))[:10])
    print("messages with package:", len(messages))

    src = open(NETMSGID, encoding="utf-8").read()
    refs = set(re.findall(r'"(proto\.[A-Za-z0-9_]+)"', src))
    print("MsgName proto.* refs:", len(refs))

    missing = sorted(r for r in refs if r not in messages)
    print("MISSING:", len(missing))
    for m in missing:
        print("  ", m)

    # Cross-check: Id entries vs MsgName keys
    id_table = src.split("NetMsgId.MsgName", 1)[0]
    ids = set(re.findall(r"^[ \t]*([a-zA-Z_0-9]+)[ \t]*=[ \t]*(-?\d+),?[ \t]*$", id_table, re.M))
    msg_keys = set(re.findall(r"^\s*\[(-?\d+)\]", src, re.M))
    print("Id entries:", len(ids), "MsgName keys:", len(msg_keys))
    id_nums = {v for _, v in ids}
    print("IDs without MsgName:", sorted(id_nums - msg_keys, key=int)[:30])
    print("MsgName without ID:", sorted(msg_keys - id_nums, key=int)[:30])


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    main()
