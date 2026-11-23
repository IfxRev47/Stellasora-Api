#!/usr/bin/env python3
"""StellaSora / Nova_Client 版 global-metadata.dat 容器工具（纯标准库）。

容器结构（由 libil2cpp.so 的 sub_3934020 反汇编确认，VA 0x3934020）：
    0x000  u32  magic = 0x1357FEDA
    0x004  u32  len   = 从 0x148 到文件尾的字节数
    0x008  256B  key/seed blob（参与校验，长度固定 0x100）
    0x108  64B   payload 摘要/签名
    0x148  8B    build tag，正常值 "CODEPHIL"（与 .rodata 0x1718917 比对）
    0x150  ...   payload（metadata 本体，进入 il2cpp 后 g_metadata = base+0x150）

用法：
  python container_tool.py parse   --file assets/global-metadata.dat
  python container_tool.py extract --file assets/global-metadata.dat --out out/payload.dat
  python container_tool.py verify  --file out/payload.dat
  python container_tool.py ksdiff  --enc assets/global-metadata.dat --plain REF.dat --out ks.bin
  python container_tool.py apply   --file enc.dat --ks ks.bin --out dec.dat
"""

from __future__ import annotations

import argparse
import struct
import sys

MAGIC = 0x1357FEDA
TAG_OFF = 0x148
TAG_LEN = 8
BLOB_OFF, BLOB_LEN = 0x008, 0x100
HASH_OFF, HASH_LEN = 0x108, 0x40
PAYLOAD_OFF = 0x150

# metadata v31 section 顺序（用于链式校验）
SECTIONS_V31 = [
    "stringLiteral", "stringLiteralData", "string", "events", "properties",
    "methods", "parameterDefaultValues", "fieldDefaultValues",
    "fieldAndParameterDefaultValueData", "fieldMarshaledSizes", "parameters",
    "fields", "genericParameters", "genericParameterConstraints",
    "genericContainers", "nestedTypes", "interfaces", "vtableMethods",
    "interfaceOffsets", "typeDefinitions", "images", "assemblies", "fieldRefs",
    "referencedAssemblies", "attributeData", "attributeDataRange",
    "unresolvedVirtualCallParameterTypes", "unresolvedVirtualCallParameterRanges",
    "unresolvedIndirectCallParameterTypes", "unresolvedIndirectCallParameterRanges",
    "windowsRuntimeTypeNames", "exportedTypeDefinitions",
]


def rd(path: str) -> bytes:
    with open(path, "rb") as f:
        return f.read()


def wr(path: str, data: bytes) -> None:
    import os
    d = os.path.dirname(os.path.abspath(path))
    if d and not os.path.isdir(d):
        os.makedirs(d, exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


# ---------------------------------------------------------------- parse
def cmd_parse(args) -> int:
    d = rd(args.file)
    if len(d) < PAYLOAD_OFF:
        print("文件过短，不是该容器")
        return 1
    magic, ln = struct.unpack_from("<II", d, 0)
    tag = d[TAG_OFF:TAG_OFF + TAG_LEN]
    print(f"文件            : {args.file}")
    print(f"大小            : {len(d):,} (0x{len(d):X})")
    print(f"magic           : 0x{magic:08X} " + ("(容器 OK)" if magic == MAGIC else "(不是该容器)"))
    print(f"len 字段        : 0x{ln:X} -> 覆盖 [0x148, 0x{0x148 + ln:X})")
    print(f"文件尾          : 0x{len(d):X} " + ("一致" if 0x148 + ln == len(d) else "不一致"))
    print(f"tag @0x148      : {tag!r}" + ("  <-- 正常" if tag == b"CODEPHIL" else "  <-- 与 .rodata 期望的 CODEPHIL 不符"))
    print(f"blob @0x008     : {d[BLOB_OFF:BLOB_OFF + 16].hex(' ')} ...")
    print(f"hash @0x108     : {d[HASH_OFF:HASH_OFF + 16].hex(' ')} ...")
    print(f"payload @0x150  : 前 8 字节 {d[PAYLOAD_OFF:PAYLOAD_OFF + 8].hex(' ')}")
    m = struct.unpack_from("<I", d, PAYLOAD_OFF)[0]
    v = struct.unpack_from("<I", d, PAYLOAD_OFF + 4)[0]
    print(f"payload magic   : 0x{m:08X} " + ("(=0xFAB11BAF 明文 metadata)" if m == 0xFAB11BAF else "(不是 0xFAB11BAF，payload 仍是密文)"))
    print(f"payload version : 0x{v:08X} (低字节 {v & 0xFF})")
    return 0


# ---------------------------------------------------------------- extract
def cmd_extract(args) -> int:
    d = rd(args.file)
    payload = d[args.off:args.off + args.size] if args.size else d[args.off:]
    if args.fix_header:
        # 仅修头 8 字节：magic + version（结构其它部分不动）
        payload = bytearray(payload)
        struct.pack_into("<I", payload, 0, 0xFAB11BAF)
        struct.pack_into("<I", payload, 4, args.version)
        payload = bytes(payload)
        print(f"[fix] 头 8 字节已改写为 magic=0xFAB11BAF version={args.version}（仅用于试探解析）")
    wr(args.out, payload)
    print(f"写出 {args.out}  {len(payload):,} 字节  offset=0x{args.off:X}")
    return 0


# ---------------------------------------------------------------- verify
def cmd_verify(args) -> int:
    d = rd(args.file)
    ok = True
    if len(d) < 0x288:
        print("[-] 太小")
        return 1
    magic, ver = struct.unpack_from("<II", d, 0)
    print(f"magic   0x{magic:08X} " + ("OK" if magic == 0xFAB11BAF else "FAIL"))
    ok &= magic == 0xFAB11BAF
    good_ver = 16 <= ver <= 40
    print(f"version {ver:<3d}      " + ("OK" if good_ver else "FAIL"))
    ok &= good_ver

    pairs = []
    for i in range(80):
        off, cnt = struct.unpack_from("<II", d, 8 + 8 * i)
        pairs.append((off, cnt))

    chain = 0
    total = 0
    for i in range(79):
        o, c = pairs[i]
        n, _ = pairs[i + 1]
        if c == 0:
            continue
        total += 1
        if o + c <= n <= o + c + 0x1000:
            chain += 1
    print(f"section 链 {chain}/{total} 命中 " + ("OK" if total and chain / total > 0.95 else "FAIL/suspect"))
    ok &= total > 0 and chain / total > 0.95

    # typeDefinitions / images 数量粗估（v31: 0x5C / 0x28 字节一条）
    td_off, td_cnt = pairs[19]
    im_off, im_cnt = pairs[20]
    print(f"typeDefinitions offset=0x{td_off:X} count={td_cnt} -> 类型约 {td_cnt // 0x5C}")
    print(f"images          offset=0x{im_off:X} count={im_cnt} -> 镜像约 {im_cnt // 0x28}")

    # stringLiteral 表链式检查
    lo, lc = pairs[0]
    if lo and lc and lo + lc <= len(d) and lc % 8 == 0:
        n = min(lc // 8, 5000)
        prev = None
        hit = 0
        for i in range(n):
            ln, di = struct.unpack_from("<II", d, lo + 8 * i)
            if prev is not None and di == prev:
                hit += 1
            prev = di + ln
        print(f"stringLiteral 表: {hit}/{n - 1} 首尾相接")
        ok &= n == 1 or hit / (n - 1) > 0.9
    print("结论:", "PASS" if ok else "FAIL")
    return 0 if ok else 2


# ---------------------------------------------------------------- ksdiff / apply
def cmd_ksdiff(args) -> int:
    enc = rd(args.enc)
    plain = rd(args.plain)
    off = args.off
    n = min(len(enc) - off, len(plain))
    ks = bytes(a ^ b for a, b in zip(enc[off:off + n], plain[:n]))
    wr(args.out, ks)
    zero = ks.count(0)
    print(f"keystream 长度 {n:,}，其中 0x00 占 {zero / n:.2%}")
    print(f"写出 {args.out}（密文 XOR 明文）")
    return 0


def cmd_apply(args) -> int:
    enc = bytearray(rd(args.file))
    ks = rd(args.ks)
    off = args.off
    n = min(len(enc) - off, len(ks))
    for i in range(n):
        enc[off + i] ^= ks[i]
    wr(args.out, bytes(enc))
    print(f"已应用 keystream {n:,} 字节 @0x{off:X} -> {args.out}")
    return 0


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)

    a = sub.add_parser("parse")
    a.add_argument("--file", required=True)
    a.set_defaults(func=cmd_parse)

    a = sub.add_parser("extract")
    a.add_argument("--file", required=True)
    a.add_argument("--out", required=True)
    a.add_argument("--off", type=lambda s: int(s, 0), default=PAYLOAD_OFF)
    a.add_argument("--size", type=lambda s: int(s, 0), default=0)
    a.add_argument("--fix-header", action="store_true")
    a.add_argument("--version", type=lambda s: int(s, 0), default=31)
    a.set_defaults(func=cmd_extract)

    a = sub.add_parser("verify")
    a.add_argument("--file", required=True)
    a.set_defaults(func=cmd_verify)

    a = sub.add_parser("ksdiff")
    a.add_argument("--enc", required=True)
    a.add_argument("--plain", required=True)
    a.add_argument("--out", required=True)
    a.add_argument("--off", type=lambda s: int(s, 0), default=PAYLOAD_OFF)
    a.set_defaults(func=cmd_ksdiff)

    a = sub.add_parser("apply")
    a.add_argument("--file", required=True)
    a.add_argument("--ks", required=True)
    a.add_argument("--out", required=True)
    a.add_argument("--off", type=lambda s: int(s, 0), default=PAYLOAD_OFF)
    a.set_defaults(func=cmd_apply)

    args = p.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
