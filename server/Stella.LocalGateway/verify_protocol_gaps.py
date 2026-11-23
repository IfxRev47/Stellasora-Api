"""Audit literal gateway dispatch/reply IDs against the client index and gaps table.

Run from any directory. This is a source inventory, not a runtime or business test.
If dispatch or replies move to other files or use computed IDs, update extraction.
"""

import collections
import json
from pathlib import Path
import re


def main():
    gateway_dir = Path(__file__).resolve().parent
    root = gateway_dir.parent.parent
    protocols = json.loads(
        (root / "reverse/output/protocol/protocols.json").read_text(encoding="utf-8-sig")
    )
    source = "\n".join(path.read_text(encoding="utf-8-sig") for path in sorted((gateway_dir / "Game").glob("LocalGameGateway*.cs")))
    gaps = (gateway_dir / "PROTOCOL_GAPS.md").read_text(encoding="utf-8-sig")
    index = {entry["id"]: entry for entry in protocols}
    assert len(index) == len(protocols), "Duplicate IDs in client index"

    # IKE and login dispatch precede the main request switch.
    assert "handshake.Id != 1" in source and "if (id == 4)" in source
    requests = {1, 4} | {int(value) for value in re.findall(r"case (-?\d+):", source)}
    replies = {int(value) for value in re.findall(r"Reply\(context, (-?\d+),", source)}
    implemented = requests | replies
    assert not implemented - index.keys(), "Unknown implemented protocol IDs"

    categories = {
        "请求": {entry["id"] for entry in protocols if entry["name"].endswith("_req")},
        "通知": {entry["id"] for entry in protocols if entry["name"].endswith("_notify")},
        "成功应答": {
            entry["id"] for entry in protocols
            if entry["name"].endswith(("_succeed_ack", "_sync_ack"))
        },
        "失败应答": {entry["id"] for entry in protocols if entry["name"].endswith("_failed_ack")},
    }
    assert sum(map(len, categories.values())) == len(index), "Unclassified protocol IDs"
    assert requests <= categories["请求"], "Dispatch includes non-request IDs"
    for label, ids in categories.items():
        done = ids & implemented
        counts = (len(ids), len(done), len(ids - implemented))
        print(f"{label}: total={counts[0]} implemented={counts[1]} missing={counts[2]}")
        row = re.search(rf"^\| {label}[^|]*\|([^\n]+)", gaps, re.MULTILINE)
        assert row, f"Missing summary row: {label}"
        cells = row[1].split("|")[:3]
        documented = tuple(int(re.search(r"\d+", cell)[0]) for cell in cells)
        assert documented == counts, f"Stale summary for {label}: {documented} != {counts}"

    section = gaps.split("## 三、", 1)[1].split("## 四、", 1)[0]
    rows = [int(value) for value in re.findall(r"^\| (-?\d+) \|", section, re.MULTILINE)]
    duplicates = [value for value, count in collections.Counter(rows).items() if count > 1]
    missing_requests = categories["请求"] - requests
    assert not duplicates, f"Duplicate request rows: {duplicates}"
    assert set(rows) == missing_requests, (
        f"Request table omissions={sorted(missing_requests - set(rows))} "
        f"extra={sorted(set(rows) - missing_requests)}"
    )
    # Validate each row's request name/type and response IDs/types as well as coverage.
    for line in section.splitlines():
        if not re.match(r"^\| -?\d+ \|", line):
            continue
        cells = [cell.strip().replace("`", "") for cell in line.split("|")[1:-1]]
        request_id = int(cells[0])
        assert cells[1] == index[request_id]["name"], f"Wrong name: {request_id}"
        assert cells[2] == index[request_id]["message"], f"Wrong request type: {request_id}"
        for cell in cells[3:5]:
            response_id, message = cell.split()
            assert index[int(response_id)]["message"] == f"proto.{message}", (
                f"Wrong response type for request {request_id}: {cell}"
            )

    total = (len(index), len(implemented), len(index.keys() - implemented))
    row = re.search(r"^\| 合计 \|([^\n]+)", gaps, re.MULTILINE)
    assert row
    assert tuple(int(re.search(r"\d+", cell)[0]) for cell in row[1].split("|")[:3]) == total
    print(f"Total: {total[0]}, implemented={total[1]}, missing={total[2]}")
    print(f"PASS: {len(rows)} request rows, no omissions or duplicates; names/types match client index")


if __name__ == "__main__":
    main()
