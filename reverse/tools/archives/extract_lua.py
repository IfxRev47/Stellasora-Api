"""Extract the version 102 StellaSora lua.arcx. Dependencies: lz4 and xxtea."""

import argparse
import os
import hashlib
import ntpath
import struct
import tempfile
from collections import Counter
from pathlib import Path, PureWindowsPath

import lz4.block
import xxtea


ARCHIVE_KEY = os.environ.get("STELLA_ARCHIVE_KEY", "").encode("utf-8")
SCRIPT_KEY = hashlib.md5(struct.pack("<I", 255 * 255)).digest()
HEADER = struct.Struct("<4s5I")
ENTRY = struct.Struct("<QIII")
DESCRIPTORS = {
    0x1A70DA105B39911E, 0x3A32D806BA986CC3,
    0xA1F6363BB22AE6FF, 0xD78A06F75FBC0761,
}


def block(data, offset, size, plain_size, compressed, encrypted):
    if offset + size > len(data):
        raise ValueError("archive block extends beyond end of file")
    raw = data[offset:offset + size]
    if encrypted:
        if not ARCHIVE_KEY:
            raise ValueError("Set STELLA_ARCHIVE_KEY for the matching client archive")
        raw = bytes(b ^ ARCHIVE_KEY[i % len(ARCHIVE_KEY)] for i, b in enumerate(raw))
    if compressed:
        raw = lz4.block.decompress(raw, uncompressed_size=plain_size)
    if len(raw) != plain_size:
        raise ValueError("archive block length mismatch")
    return raw


def lua_path(chunk, file_hash):
    # The build stores standard little-endian Lua 5.3 chunks with 8-byte size_t.
    fallback = Path("_unnamed", f"{file_hash:016x}.luac")
    if len(chunk) < 35 or chunk[4:6] != b"\x53\x00" or chunk[13] != 8:
        return fallback
    size, start = chunk[34], 35
    if size == 255:
        size, = struct.unpack_from("<Q", chunk, start)
        start += 8
    if not size or start + size - 1 > len(chunk):
        return fallback
    source = chunk[start:start + size - 1].decode("utf-8", errors="replace")
    _, marker, relative = source.replace("\\", "/").rpartition("/Lua/")
    parts = relative.split("/")
    if not marker or any(
        not part or part in {".", ".."} or part.endswith((" ", "."))
        or any(ord(c) < 32 or c in '<>:"|?*' for c in part)
        or (ntpath.isreserved(part) if hasattr(ntpath, "isreserved")
            else PureWindowsPath(part).is_reserved())
        for part in parts
    ):
        return fallback
    return Path(*parts).with_suffix(".luac")


def extract(archive, destination):
    data = archive.read_bytes()
    magic, version, flags, plain_size, size, count = HEADER.unpack_from(data)
    if magic != b"\x1aBAR" or version != 102 or flags & ~0x1111:
        raise ValueError("unsupported archive header")
    if plain_size != count * ENTRY.size:
        raise ValueError("invalid entry table length")
    table = block(data, HEADER.size, size, plain_size, flags & 1, flags & 16)

    if destination.exists():
        raise ValueError(f"output already exists, choose a new directory: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    seen = set()
    totals = Counter()
    with tempfile.TemporaryDirectory(dir=destination.parent) as temporary:
        stage = Path(temporary, "output")
        stage.mkdir()
        for file_hash, offset, original, stored in ENTRY.iter_unpack(table):
            try:
                cipher = block(data, offset, stored, original, flags & 256, flags & 4096)
                if len(cipher) < 8 or len(cipher) % 4:
                    raise ValueError("invalid XXTEA block size")
                decoded = xxtea.decrypt(cipher, SCRIPT_KEY, padding=False)
                length, = struct.unpack_from("<I", decoded, len(decoded) - 4)
                if not len(decoded) - 7 <= length <= len(decoded) - 4:
                    raise ValueError("invalid XXTEA length suffix")
                chunk = decoded[:length]
                if chunk.startswith(b"\x1bLua"):
                    name = lua_path(chunk, file_hash)
                else:
                    suffix = ".pb" if file_hash in DESCRIPTORS else ".bin"
                    name = Path("_data", f"{file_hash:016x}{suffix}")
                if name.as_posix().casefold() in seen:
                    name = Path("_duplicates", f"{file_hash:016x}{name.suffix}")
                seen.add(name.as_posix().casefold())
                target = stage / name
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("xb") as output:
                    output.write(chunk)
                totals[name.suffix] += 1
            except (ValueError, OSError, RuntimeError, struct.error) as exc:
                raise ValueError(f"entry {file_hash:016x} at {offset:#x}: {exc}") from exc
        stage.rename(destination)
    return totals


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("output", type=Path, help="new directory for extracted files")
    args = parser.parse_args()
    try:
        counts = extract(args.archive, args.output)
    except (OSError, ValueError, RuntimeError, struct.error) as error:
        parser.exit(1, f"Extraction failed: {error}\n")
    print(f"{sum(counts.values())} entries extracted to {args.output.resolve()}: {dict(counts)}")
