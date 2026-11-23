"""Export client config tables and Simplified Chinese character names."""

import argparse
import hashlib
import json
import struct
from pathlib import Path

import xxhash
import xxtea
from google.protobuf import descriptor_pb2, descriptor_pool, json_format, message_factory

from extract_lua import ENTRY, HEADER, SCRIPT_KEY, block


DEFAULT_TABLES = (
    "Character", "CharacterUpgrade", "Item", "Disc", "DiscStrengthen", "CharGem",
    "CharGemAttrValue", "Config", "Guide", "GuideGroup", "GuideMissionChapter",
    "GuideMissionQuest", "OpenFunc",
)
LANGUAGES = ("zh_CN", "zh_TW", "zh_HK", "en_US", "en", "ja_JP", "ja", "ko_KR", "ko")
EXTRA_PATHS = (
    "bubble/BubbleOffset.json", "bubble/VoResLen.json",
    "bubble/_cn/BubbleData.json", "bubble/_jp/BubbleData.json",
    "text_data/bubble/_cn/BubbleData.json", "text_data/bubble/_jp/BubbleData.json",
    "dirty_words/CharacterRegex.txt", "dirty_words/PunctuationRegex.txt",
    "dirty_words/SpaceRegex.txt", "dirty_words/DirtyWords.txt",
)


class TableArchive:
    def __init__(self, path):
        self.data = path.read_bytes()
        magic, version, flags, original, size, count = HEADER.unpack_from(self.data)
        if magic != b"\x1aBAR" or version != 102 or flags & ~0x1111:
            raise ValueError("unsupported archive header")
        directory = block(self.data, HEADER.size, size, original, flags & 1, flags & 16)
        self.entries = {key: (offset, plain, stored)
                        for key, offset, plain, stored in ENTRY.iter_unpack(directory)}
        if len(self.entries) != count:
            raise ValueError("invalid archive directory")
        self.flags = flags

    def read(self, name):
        key = xxhash.xxh64_intdigest(name.lower().encode("utf-8"))
        return self.read_entry(key)

    def read_entry(self, key):
        offset, original, size = self.entries[key]
        cipher = block(self.data, offset, size, original, self.flags & 256, self.flags & 4096)
        decoded = xxtea.decrypt(cipher, SCRIPT_KEY, padding=False)
        length, = struct.unpack_from("<I", decoded, len(decoded) - 4)
        if not len(decoded) - 7 <= length <= len(decoded) - 4:
            raise ValueError(f"invalid decoded size: {key:016x}")
        return decoded[:length]


def decode_rows(data, row_type):
    version_size, = struct.unpack_from("<H", data, 4)
    flags_at = 6 + version_size
    marker, key_type, count = struct.unpack_from("<BBI", data, flags_at)
    if (marker, key_type) not in {(0, 0), (1, 0), (1, 1), (1, 2)}:
        raise ValueError(f"unsupported table layout for {row_type.DESCRIPTOR.full_name}")
    offset = flags_at + 6
    rows = {}
    for index in range(1, count + 1):
        if marker == 0:
            key = index
        elif key_type == 0:
            key_size, = struct.unpack_from("<H", data, offset)
            offset += 2
            key = data[offset:offset + key_size].decode("utf-8")
            offset += key_size
        else:
            key_format = "<I" if key_type == 1 else "<Q"
            key, = struct.unpack_from(key_format, data, offset)
            offset += struct.calcsize(key_format)
        row_size, = struct.unpack_from("<H", data, offset)
        offset += 2
        end = offset + row_size
        if end > len(data) or key in rows:
            raise ValueError(f"invalid or duplicate row {key!r}")
        row = row_type.FromString(data[offset:end])
        rows[key] = json_format.MessageToDict(row, preserving_proto_field_name=True)
        offset = end
    if offset != len(data):
        raise ValueError(f"unconsumed data for {row_type.DESCRIPTOR.full_name}")
    return rows


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def export_archive(archive, schema, pool, output):
    candidates = list(EXTRA_PATHS)
    for message in schema.file[0].message_type:
        name = message.name
        candidates.extend((f"bin/{name}.bytes", f"json/{name}.json"))
        candidates.extend(f"language/{lang}/{name}.json" for lang in LANGUAGES)
    paths = {xxhash.xxh64_intdigest(path.lower().encode()): path for path in candidates}
    manifest = []
    for key in archive.entries:
        data = archive.read_entry(key)
        path = paths.get(key)
        kind = "text"
        count = None
        if data.startswith(b"T\x93\x03\0"):
            if path is None or not path.startswith("bin/"):
                raise ValueError(f"missing schema path for table {key:016x}")
            kind = "table"
            name = Path(path).stem
            row_type = message_factory.GetMessageClass(pool.FindMessageTypeByName(f"nova.client.{name}"))
            rows = decode_rows(data, row_type)
            count = len(rows)
            write_json(output / f"{name}.json", rows)
        elif data.lstrip().startswith((b"{", b"[")):
            kind = "json"
            json.loads(data)
        else:
            data.decode("utf-8")
        suffix = ".json" if kind == "json" else ".txt"
        raw_path = Path("raw", path or f"_unmapped/{key:016x}{suffix}")
        target = output / raw_path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        manifest.append({
            "hash": f"{key:016x}", "archive_path": path, "file": raw_path.as_posix(),
            "kind": kind, "rows": count, "size": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
        })
    write_json(output / "manifest.json", manifest)
    print(f"{len(manifest)} archive entries exported")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("schema", type=Path, help="client_table.proto descriptor set")
    parser.add_argument("output", type=Path)
    parser.add_argument("tables", nargs="*")
    parser.add_argument("--all", action="store_true", help="save and decode the entire archive")
    args = parser.parse_args()
    if args.all and args.tables:
        parser.error("use --all or give table names")
    archive = TableArchive(args.archive)
    schema = descriptor_pb2.FileDescriptorSet.FromString(args.schema.read_bytes())
    pool = descriptor_pool.DescriptorPool()
    for file in schema.file:
        pool.Add(file)
    args.output.mkdir(parents=True, exist_ok=True)
    if args.all:
        export_archive(archive, schema, pool, args.output)
    for name in ("Character",) if args.all else args.tables or DEFAULT_TABLES:
        row_type = message_factory.GetMessageClass(pool.FindMessageTypeByName(f"nova.client.{name}"))
        rows = decode_rows(archive.read(f"bin/{name}.bytes"), row_type)
        write_json(args.output / f"{name}.json", rows)
        print(f"{name}: {len(rows)}")
        if name == "Character":
            texts = json.loads(archive.read("language/zh_CN/Character.json"))
            names = {key: texts[row["Name"]] for key, row in rows.items()}
            write_json(args.output / "CharacterNames.zh_CN.json", names)


if __name__ == "__main__":
    main()
