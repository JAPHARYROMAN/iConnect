"""Validate completed AndroidJUnitRunner output without replaying a device test."""
import argparse
import json
import pathlib
import re
import sys


MAX_LOG_BYTES = 16 * 1024 * 1024


def verify_output(body, expected, expected_cases=()):
    if not 1 <= expected <= 4096:
        raise ValueError("Expected case count is outside the supported range")
    if re.search(r"FAILURES!!!|INSTRUMENTATION_FAILED|Process crashed|^INSTRUMENTATION_RESULT:\s*shortMsg=", body, re.M):
        raise ValueError("Instrumentation reports a failure or crash")
    summaries = re.findall(r"^OK[ \t]*\(([0-9]+) tests?\)[ \t]*$", body, re.M)
    if summaries != [str(expected)]:
        raise ValueError("The single JUnit success summary does not match the expected count")
    fields = {}
    starts = []
    passes = []
    terminals = []
    pending = None
    ended = False
    for line in body.splitlines():
        field = re.fullmatch(r"INSTRUMENTATION_STATUS:[ \t]*([^=]+)=(.*)", line)
        if field:
            if ended:
                raise ValueError("Test status follows the terminal instrumentation result")
            key, value = field.groups()
            if key in {"class", "test", "current", "numtests", "id"}:
                if key in fields:
                    raise ValueError("An instrumentation status field is duplicated")
                fields[key] = value
            continue
        code = re.fullmatch(r"INSTRUMENTATION_STATUS_CODE:[ \t]*(-?[0-9]+)[ \t]*", line)
        if code:
            status = int(code[1])
            if ended or status not in (0, 1):
                raise ValueError("Instrumentation contains an unsuccessful or unsupported test status")
            if not all(name in fields for name in ("class", "test", "current", "numtests", "id")):
                raise ValueError("A test status lacks its identity or counters")
            if fields["id"] != "AndroidJUnitRunner" or fields["numtests"] != str(expected):
                raise ValueError("The instrumentation runner or declared case count differs")
            identity = fields["class"] + "#" + fields["test"]
            if not fields["class"] or not fields["test"] or len(identity) > 2048:
                raise ValueError("A test identity is empty or oversized")
            if fields["current"] != str(len(passes) + 1):
                raise ValueError("Test counters are duplicated or out of order")
            if status == 1:
                if pending is not None or identity in starts:
                    raise ValueError("A test starts twice or before its predecessor completes")
                starts.append(identity)
                pending = identity
            else:
                if pending != identity:
                    raise ValueError("A passing test does not match its start record")
                passes.append(identity)
                pending = None
            fields = {}
            continue
        terminal = re.fullmatch(r"INSTRUMENTATION_CODE:[ \t]*(-?[0-9]+)[ \t]*", line)
        if terminal:
            terminals.append(int(terminal[1]))
            ended = True
    if terminals != [-1] or pending is not None or fields or len(starts) != expected or len(passes) != expected:
        raise ValueError("The expected cases and terminal result did not all complete")
    if expected_cases and sorted(passes) != sorted(expected_cases):
        raise ValueError("The completed test identities differ from the requested cases")
    return {"cases": expected, "failures": 0, "skipped": 0, "testCases": passes, "instrumentationCompleted": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=pathlib.Path)
    parser.add_argument("--expected", required=True, type=int)
    parser.add_argument("--expected-case", action="append", default=[])
    args = parser.parse_args()
    try:
        with args.log.open("rb") as stream:
            data = stream.read(MAX_LOG_BYTES + 1)
        if len(data) > MAX_LOG_BYTES:
            raise ValueError("Instrumentation log exceeds the size limit")
        body = data.decode("utf-8-sig").replace("\r\n", "\n")
        result = verify_output(body, args.expected, args.expected_case)
    except (OSError, UnicodeError, ValueError) as error:
        print(json.dumps({"verified": False, "reason": str(error)}), file=sys.stderr)
        return 2
    print(json.dumps({"verified": True, **result}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
