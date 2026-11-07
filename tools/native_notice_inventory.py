"""Preserve notice candidates from verified source archives without extracting source paths.

The PowerShell entry point verifies the source bundle before invoking this helper.
This is an inventory for review, not a license detector or distribution approval.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import stat
import zipfile
from pathlib import Path

MAX_MEMBERS = 20_000
MAX_EXPANDED = 512 * 1024 * 1024
MAX_TEXT = 2 * 1024 * 1024
MAX_BLOCK = 128 * 1024
MAX_EXPORTED = 32 * 1024 * 1024
KEYWORDS = re.compile(r"copyright|SPDX-License-Identifier|redistribution|all rights reserved|licen[cs]ed under", re.I)
DOCUMENT = re.compile(r"^(?:licen[cs]e|copying|notice|copyright|authors|patents)(?:[._-].*)?$", re.I)
TEXT_EXTENSIONS = {".c", ".h", ".cc", ".cpp", ".hpp", ".in", ".txt", ".md", ".rst", ".pl", ".pm", ".cmake", ".s", ".asm", ".py", ".rs", ".js", ".html", ".sh"}


def sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def file_sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def comment_blocks(text: str) -> list[str]:
    """Keep complete matching comment blocks, including later contributor notices.

    This deliberately does not pretend to parse every source language. Keywords
    outside recognized comments are listed separately for a human to inspect.
    """
    result: list[str] = []
    for opening, closing in (("/*", "*/"), ("<!--", "-->")):
        at = 0
        while (begin := text.find(opening, at)) >= 0:
            end = text.find(closing, begin + len(opening))
            if end < 0:
                break
            block = text[begin:end + len(closing)]
            if KEYWORDS.search(block):
                result.append(block)
            at = end + len(closing)
    pending: list[str] = []
    for line in text.splitlines(keepends=True) + [""]:
        if line.lstrip().startswith(("//", "#", ";")):
            pending.append(line)
        elif pending:
            block = "".join(pending)
            if KEYWORDS.search(block):
                result.append(block)
            pending = []
    return result


def safe_member(name: str, prefix: str) -> None:
    if len(name) > 2048 or not name.startswith(prefix) or "\\" in name or ":" in name or any(ord(c) < 32 for c in name):
        raise ValueError("Unexpected source archive path")
    parts = name.rstrip("/").split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise ValueError("Unexpected source archive path segment")


def collect_component(root: Path, component: dict, documents: Path) -> dict:
    if not re.fullmatch(r"[a-z0-9_-]{1,32}", component["name"]) or component["archive"] != "sources/" + component["name"] + ".zip":
        raise ValueError("Invalid component name or archive binding")
    archive = root / component["archive"]
    if file_sha(archive) != component["archiveSha256"]:
        raise ValueError("Source archive differs from its verified hash")
    rows: list[dict] = []
    blocks: dict[str, dict] = {}
    skipped: list[dict] = []
    total_text = 0
    exported_bytes = 0
    with zipfile.ZipFile(archive) as source:
        entries = source.infolist()
        if len(entries) != component["entries"] or len(entries) > MAX_MEMBERS or sum(e.file_size for e in entries) > MAX_EXPANDED:
            raise ValueError("Source archive exceeds its verified inventory or resource limits")
        seen: set[str] = set()
        for entry in entries:
            safe_member(entry.filename, component["prefix"])
            if entry.filename in seen:
                raise ValueError("Duplicated source archive member")
            seen.add(entry.filename)
            if entry.is_dir():
                continue
            is_document = bool(DOCUMENT.fullmatch(Path(entry.filename).name))
            if not is_document and Path(entry.filename).suffix.lower() not in TEXT_EXTENSIONS:
                continue
            mode = (entry.external_attr >> 16) & 0xF000
            if mode == stat.S_IFLNK or entry.file_size > MAX_TEXT:
                skipped.append({"path": entry.filename, "reason": "link or oversized text candidate"})
                continue
            total_text += entry.file_size
            if total_text > MAX_EXPANDED:
                raise ValueError("Text inventory exceeds its resource limit")
            raw = source.read(entry)  # zipfile validates the member CRC.
            try:
                text = raw.decode("utf-8-sig")
            except UnicodeError:
                skipped.append({"path": entry.filename, "reason": "not strict UTF-8"})
                continue
            if "\0" in text:
                skipped.append({"path": entry.filename, "reason": "binary text candidate"})
                continue
            extracted = comment_blocks(text)
            accepted = []
            for block in extracted:
                encoded = block.encode("utf-8")
                if len(encoded) > MAX_BLOCK:
                    skipped.append({"path": entry.filename, "reason": "oversized notice block"})
                    continue
                digest = sha(encoded)
                if digest not in blocks:
                    exported_bytes += len(encoded)
                accepted.append(digest)
                item = blocks.setdefault(digest, {"sha256": digest, "text": block, "sources": []})
                if entry.filename not in item["sources"]:
                    item["sources"].append(entry.filename)
            if is_document:
                exported_bytes += len(raw)
                target = f"{component['name']}-{sha(entry.filename.encode('utf-8'))}.txt"
                with (documents / target).open("xb") as stream:
                    stream.write(raw)
                rows.append({"path": entry.filename, "output": "documents/" + target, "sha256": sha(raw)})
            if exported_bytes > MAX_EXPORTED:
                raise ValueError("Exported notice candidates exceed their resource limit")
            unmatched = [n for n, line in enumerate(text.splitlines(), 1)
                         if KEYWORDS.search(line) and not is_document and not any(line in block for block in extracted)]
            tags = sorted(set(re.findall(r"SPDX-License-Identifier:\s*([^\r\n*<>]{1,200})", text)))
            if accepted or tags or unmatched:
                rows.append({"path": entry.filename, "sourceSha256": sha(raw), "blocks": sorted(set(accepted)),
                             "spdxExpressions": tags, "unclassifiedKeywordLines": unmatched})
        root_member = source.getinfo(component["rootMember"])
        if ((root_member.external_attr >> 16) & 0xF000) == stat.S_IFLNK:
            raise ValueError("The root license is a redirected source entry")
        if root_member.file_size > MAX_TEXT or sha(source.read(root_member)) != component["rootLicenseSha256"]:
            raise ValueError("The root license differs from its verified source archive")
    if file_sha(archive) != component["archiveSha256"]:
        raise ValueError("Source archive changed during notice inventory")
    return {"name": component["name"], "commit": component["commit"], "scope": component["scope"],
            "archiveSha256": component["archiveSha256"], "rootLicenseSha256": component["rootLicenseSha256"],
            "sourceEntries": len(entries), "textBytesExamined": total_text, "records": rows,
            "noticeBlocks": list(blocks.values()), "skippedTextCandidates": skipped}


def inventory(verified: dict, output: Path) -> dict:
    if output.exists():
        raise ValueError("The output directory already exists")
    root = Path(verified["directory"]).resolve(strict=True)
    resolved_output = output.resolve(strict=False)
    if resolved_output == root or root in resolved_output.parents:
        raise ValueError("Notice output must be separate from the source bundle")
    if not 1 <= len(verified["components"]) <= 7 or sum(c["entries"] for c in verified["components"]) > MAX_MEMBERS:
        raise ValueError("The complete source inventory exceeds its resource limit")
    output.mkdir(parents=True, exist_ok=False)
    documents = output / "documents"
    documents.mkdir()
    components = [collect_component(root, c, documents) for c in verified["components"]]
    exported = sum(len(block["text"].encode("utf-8")) for c in components for block in c["noticeBlocks"])
    exported += sum(path.stat().st_size for path in documents.iterdir())
    if exported > MAX_EXPORTED:
        raise ValueError("Combined notice blocks exceed their resource limit")
    notice = ["iConnect native transport source-notice inventory\n",
              "Candidate DLL SHA-256: " + verified["binarySha256"] + "\n",
              "Includes archive examples and excluded source inputs. Human scope and license review remain required.\n\n"]
    for component in components:
        notice.append("\nCOMPONENT: " + component["name"] + " @ " + component["commit"] + "\n")
        for row in component["records"]:
            if "output" in row:
                notice.append("\nDOCUMENT: " + row["path"] + "\n" + (output / row["output"]).read_bytes().decode("utf-8-sig") + "\n")
        for block in component["noticeBlocks"]:
            notice.append("\nSOURCE NOTICE: " + ", ".join(block["sources"]) + "\n" + block["text"] + "\n")
    (output / "SOURCE_NOTICES.txt").write_text("".join(notice), encoding="utf-8", newline="\n")
    if file_sha(root / "bundle-evidence.json") != verified["manifestSha256"] or file_sha(root / "datachannel.dll") != verified["binarySha256"]:
        raise ValueError("The verified bundle binding changed")
    for component in verified["components"]:
        if file_sha(root / component["archive"]) != component["archiveSha256"] or file_sha(root / component["rootLicense"]) != component["rootLicenseSha256"]:
            raise ValueError("A source or root notice changed during inventory")
    for path in documents.iterdir():
        expected = next(row["sha256"] for c in components for row in c["records"] if row.get("output") == "documents/" + path.name)
        if file_sha(path) != expected:
            raise ValueError("An exported notice changed")
    report = {"schema": 1, "stage": "NativeNoticeInventory", "binarySha256": verified["binarySha256"],
              "bundleEvidenceSha256": verified["manifestSha256"], "components": components,
              "sourceNoticesSha256": file_sha(output / "SOURCE_NOTICES.txt"), "licenseApproval": False,
              "completeLicenseCoverageVerified": False, "actualLinkerClosureVerified": False,
              "distributionApproved": False}
    # This completed marker is written only after output and input read-back.
    with (output / "notice-inventory.json").open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verified-input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.verified_input.stat().st_size > 64 * 1024:
        raise ValueError("The verified inventory input exceeds its resource limit")
    verified = json.loads(args.verified_input.read_text(encoding="utf-8-sig"))
    result = inventory(verified, args.output)
    print(json.dumps({"stage": result["stage"], "components": len(result["components"]),
                      "noticeBlocks": sum(len(c["noticeBlocks"]) for c in result["components"]),
                      "report": str(args.output / "notice-inventory.json")}))


if __name__ == "__main__":
    main()
