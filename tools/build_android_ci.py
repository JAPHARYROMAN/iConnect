#!/usr/bin/env python3
"""Run every Android CI check; retry only an identified upstream download failure."""

import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

TASKS = (
    ":app:assembleDebug",
    ":app:assembleDebugAndroidTest",
    ":app:assembleRelease",
    ":app:testDebugUnitTest",
    ":app:lintDebug",
)
MAX_CLASSIFICATION_BYTES = 8 * 1024 * 1024
NETWORK_FAILURE = re.compile(
    r"(?:HTTP response code[: ]+|(?:Received )?status code[: ]+|HTTP[/ ]\S*\s+)"
    r"(?:429|500|502|503|504)\b|SocketTimeoutException|ReadTimeoutException|"
    r"(?:connect(?:ion)?|read)\s+timed out|Connection reset(?: by peer)?",
    re.IGNORECASE,
)
DEPENDENCY_FAILURE = re.compile(
    r"Could not (?:resolve all (?:files|artifacts|dependencies)|download|get resource|resolve [\w.-]+:)",
    re.IGNORECASE,
)
NON_DOWNLOAD_FAILURE = re.compile(
    r"(?:^|\n)e:\s|(?:^|\n).*\berror:\s|Compilation (?:error|failed)|Unresolved reference|"
    r"There were failing tests|\btests? failed\b|Lint found|OutOfMemoryError|"
    r"daemon disappeared|(?:checksum|signature|dependency verification|PKIX|certificate).{0,120}(?:fail|mismatch|invalid)|"
    r"Verification of Gradle distribution failed|No matching variant|Unsupported class file",
    re.IGNORECASE,
)


def retryable_download_failure(output):
    """Require every reported build failure to be a transient artifact fetch."""
    if len(output.encode("utf-8")) > MAX_CLASSIFICATION_BYTES or NON_DOWNLOAD_FAILURE.search(output):
        return False
    summaries = re.findall(r"(?ms)^\* What went wrong:\s*\n(.*?)(?=^\* Try:|\Z)", output)
    if summaries:
        return all(DEPENDENCY_FAILURE.search(part) and NETWORK_FAILURE.search(part) for part in summaries)
    # Wrapper bootstrap fails before Gradle can report a task or failure summary.
    return bool(
        not re.search(r"(?m)^> Task\s", output)
        and re.search(r'Exception in thread "main" java\.io\.IOException:', output)
        and "https://services.gradle.org/distributions/gradle-" in output
        and NETWORK_FAILURE.search(output)
    )


def gradle_command(android):
    wrapper = [str(android / "gradlew.bat")] if os.name == "nt" else ["bash", str(android / "gradlew")]
    return wrapper + list(TASKS) + ["--no-daemon", "--console=plain"]


def run_build(command, android, reports, max_attempts=3, retry_delay=5, sleep=time.sleep):
    reports.mkdir(parents=True, exist_ok=False)
    for attempt in range(1, max_attempts + 1):
        log = reports / f"attempt-{attempt}.log"
        print(f"Android CI attempt {attempt}/{max_attempts}; log: {log}", flush=True)
        with log.open("w", encoding="utf-8") as saved:
            with subprocess.Popen(command, cwd=android, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  text=True, encoding="utf-8", errors="replace", shell=False) as process:
                try:
                    for line in process.stdout:
                        saved.write(line)
                        print(line, end="", flush=True)
                    code = process.wait()
                except BaseException:
                    # Terminate only the child owned by this invocation; never launch another attempt.
                    process.terminate()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()
                    raise
        if code == 0:
            return 0
        output = log.read_text(encoding="utf-8") if log.stat().st_size <= MAX_CLASSIFICATION_BYTES else ""
        if attempt == max_attempts or not retryable_download_failure(output):
            print(f"Android CI failed; preserved attempt: {log}", flush=True)
            return code if code > 0 else 1
        print("An upstream download failed transiently; retrying the same pinned build and all checks.", flush=True)
        sleep(min(retry_delay * attempt, 30))
    raise AssertionError("The bounded attempt loop did not return.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--max-attempts", type=int, choices=(1, 2, 3), default=3)
    args = parser.parse_args()
    android = Path(__file__).resolve().parents[1] / "apps" / "android"
    reports = android / "build" / "reports" / "ci-attempts" / uuid.uuid4().hex
    return run_build(gradle_command(android), android, reports, max_attempts=args.max_attempts)


if __name__ == "__main__":
    sys.exit(main())
