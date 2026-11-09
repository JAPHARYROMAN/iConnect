import importlib.util
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_android_instrumentation", ROOT / "tools/verify_android_instrumentation.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CASE = "com.iconnect.proof.LargeFileTest#uploadAndIncomingProviderOver4GiBReopenTheOriginalReceipts"


def output(count=1):
    records = []
    for current in range(1, count + 1):
        for code in (1, 0):
            records.append(f"""INSTRUMENTATION_STATUS: class=Fixture
INSTRUMENTATION_STATUS: test=case{current}
INSTRUMENTATION_STATUS: current={current}
INSTRUMENTATION_STATUS: numtests={count}
INSTRUMENTATION_STATUS: id=AndroidJUnitRunner
INSTRUMENTATION_STATUS_CODE: {code}
""")
    return "".join(records) + f"\nOK ({count} {'test' if count == 1 else 'tests'})\nINSTRUMENTATION_CODE: -1\n"


class InstrumentationResultTests(unittest.TestCase):
    def test_captured_single_large_file_case_completes(self):
        body = (ROOT / "tests/fixtures/android/single-case-success.txt").read_text(encoding="utf-8-sig")
        result = MODULE.verify_output(body, 1, [CASE])
        self.assertEqual(result["cases"], 1)
        self.assertTrue(result["instrumentationCompleted"])

    def test_plural_summary_completes_all_cases(self):
        self.assertEqual(MODULE.verify_output(output(3), 3)["cases"], 3)

    def test_summary_alone_is_insufficient(self):
        with self.assertRaisesRegex(ValueError, "did not all complete"):
            MODULE.verify_output("OK (1 test)\nINSTRUMENTATION_CODE: -1\n", 1)

    def test_wrong_expected_count_is_refused(self):
        with self.assertRaisesRegex(ValueError, "summary"):
            MODULE.verify_output(output(1), 2)

    def test_duplicate_success_summary_is_refused(self):
        with self.assertRaisesRegex(ValueError, "summary"):
            MODULE.verify_output(output(1) + "OK (1 test)\n", 1)

    def test_failure_status_cannot_be_overridden_by_success_text(self):
        for code in (-1, -2, -3, -4, 2):
            with self.subTest(code=code), self.assertRaisesRegex(ValueError, "unsuccessful"):
                MODULE.verify_output(output(1).replace("STATUS_CODE: 0", f"STATUS_CODE: {code}"), 1)

    def test_missing_terminal_result_is_refused(self):
        with self.assertRaisesRegex(ValueError, "did not all complete"):
            MODULE.verify_output(output(1).replace("INSTRUMENTATION_CODE: -1", ""), 1)

    def test_duplicate_terminal_result_is_refused(self):
        with self.assertRaisesRegex(ValueError, "did not all complete"):
            MODULE.verify_output(output(1) + "INSTRUMENTATION_CODE: -1\n", 1)

    def test_wrong_completed_case_is_refused(self):
        with self.assertRaisesRegex(ValueError, "identities differ"):
            MODULE.verify_output(output(1), 1, [CASE])

    def test_status_without_start_is_refused(self):
        body = output(1)
        body = body[body.index("INSTRUMENTATION_STATUS_CODE: 1") + len("INSTRUMENTATION_STATUS_CODE: 1\n"):]
        with self.assertRaisesRegex(ValueError, "does not match its start"):
            MODULE.verify_output(body, 1)

    def test_repeated_case_counters_are_refused(self):
        with self.assertRaisesRegex(ValueError, "out of order"):
            MODULE.verify_output(output(2).replace("current=2", "current=1"), 2)

    def test_crash_cannot_be_overridden_by_success_text(self):
        with self.assertRaisesRegex(ValueError, "failure or crash"):
            MODULE.verify_output(output(1) + "INSTRUMENTATION_RESULT: shortMsg=Process crashed\n", 1)


if __name__ == "__main__":
    unittest.main()
