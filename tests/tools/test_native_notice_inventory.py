import importlib.util
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "tools" / "native_notice_inventory.py"
SPEC = importlib.util.spec_from_file_location("native_notice_inventory", SOURCE)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class NoticeInventoryTests(unittest.TestCase):
    def fixture(self, root, extra=None):
        (root / "sources").mkdir()
        (root / "notices").mkdir()
        license_bytes = b"Generated root license for fixture authors.\n"
        archive = root / "sources/libjuice.zip"
        with zipfile.ZipFile(archive, "w") as stream:
            stream.writestr("libjuice/LICENSE", license_bytes)
            stream.writestr("libjuice/example.c", "/* Copyright fixture author A. All rights reserved. */\nint x;\n/* Copyright fixture author B. Redistribution permitted by fixture terms. */\n")
            if extra:
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", UserWarning)
                    stream.writestr(*extra)
        (root / "notices/libjuice-LICENSE").write_bytes(license_bytes)
        (root / "datachannel.dll").write_bytes(b"Generated, unexecuted fixture bytes.")
        (root / "bundle-evidence.json").write_text("{}")
        return {"directory": str(root), "binarySha256": MODULE.file_sha(root / "datachannel.dll"),
                "manifestSha256": MODULE.file_sha(root / "bundle-evidence.json"), "components": [{
                    "name": "libjuice", "commit": "0" * 40, "scope": "required", "archive": "sources/libjuice.zip",
                    "archiveSha256": MODULE.file_sha(archive), "entries": 3 if extra else 2, "prefix": "libjuice/",
                    "rootMember": "libjuice/LICENSE", "rootLicense": "notices/libjuice-LICENSE", "rootLicenseSha256": MODULE.sha(license_bytes)}]}

    def test_later_notices_are_preserved_and_inventory_never_grants_approval(self):
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            root = base / "bundle"
            root.mkdir()
            output = base / "review"
            verified = self.fixture(root)
            report = MODULE.inventory(verified, output)
            notices = (output / "SOURCE_NOTICES.txt").read_text()
            self.assertIn("fixture author A", notices)
            self.assertIn("fixture author B", notices)
            self.assertIn("Generated root license", notices)
            self.assertEqual(2, len(report["components"][0]["noticeBlocks"]))
            for field in ("licenseApproval", "distributionApproved", "actualLinkerClosureVerified", "completeLicenseCoverageVerified"):
                self.assertIs(False, report[field])
            self.assertEqual(report["binarySha256"], verified["binarySha256"])
            self.assertEqual(report, json.loads((output / "notice-inventory.json").read_text()))

    def test_traversal_and_duplicate_members_never_produce_a_completed_report(self):
        for extra in (("libjuice/../../escaped.txt", "bad"), ("libjuice/example.c", "duplicate")):
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as directory:
                base = pathlib.Path(directory)
                root = base / "bundle"
                root.mkdir()
                verified = self.fixture(root, extra)
                with self.assertRaises(ValueError):
                    MODULE.inventory(verified, base / "review")
                self.assertFalse((base / "review/notice-inventory.json").exists())
                self.assertFalse((base / "escaped.txt").exists())

    def test_changed_archive_and_root_notice_fail_without_completion(self):
        for mutate in ("archive", "root"):
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as directory:
                base = pathlib.Path(directory)
                root = base / "bundle"
                root.mkdir()
                verified = self.fixture(root)
                path = root / ("sources/libjuice.zip" if mutate == "archive" else "notices/libjuice-LICENSE")
                path.write_bytes(b"changed")
                with self.assertRaises(ValueError):
                    MODULE.inventory(verified, base / "review")
                self.assertFalse((base / "review/notice-inventory.json").exists())

    def test_existing_or_nested_output_is_kept(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            verified = self.fixture(root)
            for output in (root, root / "nested-review"):
                with self.assertRaises(ValueError):
                    MODULE.inventory(verified, output)
            self.assertFalse((root / "nested-review").exists())

    def test_unclassified_and_oversized_text_remain_explicit_review_items(self):
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            root = base / "bundle"
            root.mkdir()
            verified = self.fixture(root, ("libjuice/oversized.c", "x" * 1024))
            with patch.object(MODULE, "MAX_TEXT", 512):
                report = MODULE.inventory(verified, base / "review")
            self.assertEqual([{"path": "libjuice/oversized.c", "reason": "link or oversized text candidate"}],
                             report["components"][0]["skippedTextCandidates"])
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            root = base / "bundle"
            root.mkdir()
            verified = self.fixture(root, ("libjuice/string.c", 'const char *x="Copyright generated fixture string.";\n'))
            report = MODULE.inventory(verified, base / "review")
            row = next(row for row in report["components"][0]["records"] if row["path"].endswith("string.c"))
            self.assertEqual([1], row["unclassifiedKeywordLines"])
            self.assertEqual([], row["blocks"])


if __name__ == "__main__":
    unittest.main()
