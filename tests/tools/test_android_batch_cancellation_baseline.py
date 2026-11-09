import hashlib
import importlib.util
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "batch_baseline", ROOT / "tools" / "verify_android_batch_cancellation_baseline.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CAPTURE = ROOT / "docs/development/evidence/android-batch-cancellation-baseline-2026-11-09.txt"


class BatchCancellationBaselineTests(unittest.TestCase):
    def setUp(self):
        self.raw = CAPTURE.read_bytes()

    def test_actual_device_capture_reproduces_only_the_baseline(self):
        result = MODULE.verify_baseline(self.raw, 0)
        self.assertTrue(result["baselineReproduced"])
        self.assertEqual(result["observedStatus"], "INVALID_REQUEST")
        self.assertEqual(result["expectedAssertionFailures"], 1)
        self.assertFalse(result["productionSuccessProven"])
        self.assertEqual(
            hashlib.sha256(self.raw).hexdigest(),
            "f5e7d4f9f1c27f864360f04426aa393bab0bc7dea58f806801b5ecd5b97c3b29",
        )

    def refuse(self, raw, exit_code=0):
        with self.assertRaises(ValueError):
            MODULE.verify_baseline(raw, exit_code)

    def test_other_refusal_is_not_the_baseline(self):
        self.refuse(self.raw.replace(b"<INVALID_REQUEST>", b"<UNSUPPORTED>"))

    def test_unrelated_assertion_is_not_the_baseline(self):
        self.refuse(self.raw.replace(b"Stopping a saved batch check must be supported.", b"Copies changed."))

    def test_timeout_is_not_the_baseline(self):
        self.refuse(self.raw.replace(b"java.lang.AssertionError", b"kotlinx.coroutines.TimeoutCancellationException"))

    def test_pass_or_skip_is_not_the_baseline(self):
        for code in (b"0", b"-3", b"-4"):
            with self.subTest(code=code):
                self.refuse(self.raw.replace(b"INSTRUMENTATION_STATUS_CODE: -2", b"INSTRUMENTATION_STATUS_CODE: " + code))

    def test_duplicate_capture_is_rejected(self):
        self.refuse(self.raw + self.raw)

    def test_incomplete_capture_is_rejected(self):
        self.refuse(self.raw.split(b"INSTRUMENTATION_CODE: -1")[0])

    def test_other_test_is_rejected(self):
        for original in (MODULE.TEST_CLASS, MODULE.TEST_METHOD):
            with self.subTest(original=original):
                self.refuse(self.raw.replace(original.encode(), b"otherTest"))

    def test_adb_error_is_rejected(self):
        self.refuse(self.raw, exit_code=1)

    def test_runner_crash_is_rejected(self):
        self.refuse(self.raw + b"\nProcess crashed\n")

    def test_oversized_capture_is_rejected(self):
        self.refuse(self.raw + b" " * MODULE.MAX_LOG_BYTES)

    def test_invalid_utf8_is_rejected(self):
        self.refuse(self.raw + b"\xff")


if __name__ == "__main__":
    unittest.main()
