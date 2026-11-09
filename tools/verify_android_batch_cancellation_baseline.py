"""Verify the captured pre-operation-47 refusal; this does not prove a fixed build passes."""

import argparse
import json
import pathlib
import re


MAX_LOG_BYTES = 1024 * 1024
TEST_CLASS = "com.iconnect.proof.PublishedBatchCheckTest"
TEST_METHOD = "cancelingSavedBatchCheckReleasesBlockedReadsAndPreservesCopiesAndReceipts"
ASSERTION = (
    "java.lang.AssertionError: Stopping a saved batch check must be supported. "
    "expected:<FILE_STATUS_OK> but was:<INVALID_REQUEST>"
)


def verify_baseline(raw: bytes, adb_exit_code: int) -> dict:
    """Accept only the one captured unknown-operation assertion and complete runner result."""
    if adb_exit_code != 0 or len(raw) > MAX_LOG_BYTES:
        raise ValueError("ADB must finish successfully with a bounded instrumentation log.")
    body = raw.decode("utf-8-sig").replace("\r\n", "\n")
    if re.search(r"TimeoutCancellationException|INSTRUMENTATION_FAILED|Process crashed", body):
        raise ValueError("Timeout, runner failure or crash does not reproduce the baseline.")

    def fields(name: str) -> list[str]:
        return re.findall(r"^INSTRUMENTATION_STATUS: " + name + r"=([^\n]*)$", body, re.M)

    codes = re.findall(r"^INSTRUMENTATION_STATUS_CODE: (-?\d+)$", body, re.M)
    terminal = re.findall(r"^INSTRUMENTATION_CODE: (-?\d+)$", body, re.M)
    summaries = re.findall(r"^Tests run: (\d+),\s+Failures: (\d+)$", body, re.M)
    errors = re.findall(r"^(?:INSTRUMENTATION_STATUS: stack=)?(java\.lang\.AssertionError:[^\n]+)$", body, re.M)
    if (
        codes != ["1", "-2"]
        or terminal != ["-1"]
        or not body.rstrip().endswith("INSTRUMENTATION_CODE: -1")
        or fields("class") != [TEST_CLASS, TEST_CLASS]
        or fields("test") != [TEST_METHOD, TEST_METHOD]
        or fields("numtests") != ["1", "1"]
        or fields("current") != ["1", "1"]
        or fields("stack") != [ASSERTION]
        or errors != [ASSERTION, ASSERTION, ASSERTION]
        or summaries != [("1", "1")]
        or body.count("\nFAILURES!!!\n") != 1
        or body.count(f"\n1) {TEST_METHOD}({TEST_CLASS})\n") != 1
        or re.search(r"^OK\s*\(", body, re.M)
    ):
        raise ValueError("The log is not the exact, complete operation-47 baseline refusal.")
    return {
        "baselineReproduced": True,
        "test": f"{TEST_CLASS}#{TEST_METHOD}",
        "testsRun": 1,
        "expectedAssertionFailures": 1,
        "observedStatus": "INVALID_REQUEST",
        "productionSuccessProven": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=pathlib.Path)
    parser.add_argument("--adb-exit-code", required=True, type=int)
    args = parser.parse_args()
    try:
        with args.log.open("rb") as stream:
            result = verify_baseline(stream.read(MAX_LOG_BYTES + 1), args.adb_exit_code)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Baseline not reproduced: {error}\n")
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
