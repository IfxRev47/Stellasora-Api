"""Batch-decompile Lua chunks with unluac, preserving their relative paths."""

import argparse
import os
import subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--jar", type=Path, required=True)
    parser.add_argument("--java", default="java")
    parser.add_argument("--jobs", type=int, default=min(8, os.cpu_count() or 1))
    args = parser.parse_args()
    chunks = sorted(args.source.rglob("*.luac"))
    if not chunks or args.jobs < 1 or not args.jar.is_file():
        parser.error("provide an input directory with .luac files, an unluac JAR, and at least one job")
    command = [args.java, "-Dfile.encoding=UTF-8", "-jar", str(args.jar)]

    def decompile(chunk):
        target = args.output / chunk.relative_to(args.source).with_suffix(".lua")
        try:
            result = subprocess.run(command + [str(chunk)], capture_output=True, timeout=90)
            if result.returncode:
                detail = result.stderr.decode("utf-8", errors="replace").strip()
                return chunk, detail or f"unluac exited with code {result.returncode}"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(result.stdout)
        except (OSError, subprocess.TimeoutExpired) as error:
            return chunk, str(error)
        return None

    failures = []
    with ThreadPoolExecutor(max_workers=args.jobs) as executor:
        for index, failure in enumerate(executor.map(decompile, chunks), 1):
            if failure:
                failures.append(failure)
            if index % 250 == 0 or index == len(chunks):
                print(f"{index}/{len(chunks)} processed, {len(failures)} failed", flush=True)
    for chunk, error in failures:
        print(f"{chunk}: {error}")
    return bool(failures)


if __name__ == "__main__":
    raise SystemExit(main())
