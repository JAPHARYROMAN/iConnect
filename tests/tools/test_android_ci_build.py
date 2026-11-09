import contextlib
import importlib.util
import io
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "tools" / "build_android_ci.py"
SPEC = importlib.util.spec_from_file_location("build_android_ci", SOURCE)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

DEPENDENCY_504 = """FAILURE: Build failed with an exception.
* What went wrong:
Execution failed for task ':app:compileDebugKotlin'.
> Could not resolve all files for configuration ':app:kotlinBuildToolsApiClasspath'.
  > Could not download kotlin-compiler-embeddable-2.3.0.jar
    > Could not get resource 'https://repo.maven.apache.org/maven2/generated.jar'.
      > Could not GET 'https://github.com/JetBrains/kotlin/releases/download/v2.3.0/generated.jar'. Received status code 504 from server: Gateway Time-out
* Try:
Run with --stacktrace.
"""
WRAPPER_504 = """Downloading https://services.gradle.org/distributions/gradle-8.13-bin.zip
Exception in thread "main" java.io.IOException: Server returned HTTP response code: 504 for URL: https://services.gradle.org/distributions/gradle-8.13-bin.zip
"""


class AndroidCiBuildTests(unittest.TestCase):
    def test_actual_dependency_and_wrapper_failure_shapes_are_retryable(self):
        for output in (DEPENDENCY_504, WRAPPER_504, DEPENDENCY_504.replace("504", "503"),
                       DEPENDENCY_504.replace("Received status code 504 from server: Gateway Time-out", "java.net.SocketTimeoutException: Read timed out")):
            with self.subTest(output=output):
                self.assertTrue(MODULE.retryable_download_failure(output))

    def test_compiler_test_lint_integrity_and_other_failures_are_not_retried(self):
        extra = ("e: file:///Generated.kt: Unresolved reference", "There were failing tests",
                 "Lint found errors in the project", "java.lang.OutOfMemoryError",
                 "Dependency verification failed", "Checksum mismatch", "PKIX path building failed")
        for failure in extra:
            with self.subTest(failure=failure):
                self.assertFalse(MODULE.retryable_download_failure(failure + "\n" + DEPENDENCY_504))
        self.assertFalse(MODULE.retryable_download_failure(DEPENDENCY_504.replace("504", "404")))
        self.assertFalse(MODULE.retryable_download_failure("An application test printed HTTP response code: 504"))

    def test_every_failure_in_a_multi_failure_build_must_be_an_artifact_download(self):
        compile_error = """* What went wrong:
Execution failed for task ':app:compileReleaseKotlin'.
> Compilation error. See log for more details.
* Try:
"""
        self.assertFalse(MODULE.retryable_download_failure(DEPENDENCY_504 + compile_error))
        self.assertTrue(MODULE.retryable_download_failure(DEPENDENCY_504 + DEPENDENCY_504))

    def test_a_wrapper_shaped_error_after_tasks_does_not_restart_the_build(self):
        self.assertFalse(MODULE.retryable_download_failure("> Task :app:testDebugUnitTest FAILED\n" + WRAPPER_504))

    def test_excessive_output_is_not_classified_from_an_incomplete_tail(self):
        self.assertFalse(MODULE.retryable_download_failure("x" * MODULE.MAX_CLASSIFICATION_BYTES + DEPENDENCY_504))

    def invoke_child(self, first_output, attempts=3, always_fail=False):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            child = root / "generated_builder.py"
            child.write_text("""import pathlib, sys
counter=pathlib.Path('counter')
value=int(counter.read_text())+1 if counter.exists() else 1
counter.write_text(str(value))
failed=value==1 or ALWAYS_FAIL
print(FIRST if failed else 'BUILD SUCCESSFUL', flush=True)
sys.exit(1 if failed else 0)
""".replace("FIRST", repr(first_output)).replace("ALWAYS_FAIL", repr(always_fail)), encoding="utf-8")
            waits = []
            with contextlib.redirect_stdout(io.StringIO()):
                code = MODULE.run_build([sys.executable, str(child)], root, root / "reports", attempts, sleep=waits.append)
            return code, int((root / "counter").read_text()), waits, sorted(x.name for x in (root / "reports").iterdir())

    def test_a_real_child_retries_a_download_and_preserves_both_attempts(self):
        self.assertEqual((0, 2, [5], ["attempt-1.log", "attempt-2.log"]), self.invoke_child(DEPENDENCY_504))

    def test_a_real_failed_test_is_returned_without_another_attempt(self):
        self.assertEqual((1, 1, [], ["attempt-1.log"]), self.invoke_child("There were failing tests\n" + DEPENDENCY_504))

    def test_the_attempt_limit_preserves_failure(self):
        self.assertEqual((1, 1, [], ["attempt-1.log"]), self.invoke_child(DEPENDENCY_504, attempts=1))

    def test_repeated_upstream_failure_stops_after_three_attempts(self):
        self.assertEqual((1, 3, [5, 10], ["attempt-1.log", "attempt-2.log", "attempt-3.log"]),
                         self.invoke_child(DEPENDENCY_504, always_fail=True))

    def test_the_fixed_command_retains_every_existing_android_check(self):
        required = [":app:assembleDebug", ":app:assembleDebugAndroidTest", ":app:assembleRelease",
                    ":app:testDebugUnitTest", ":app:lintDebug"]
        for platform, android, launcher in (("posix", pathlib.PurePosixPath("generated/android"), "bash"),
                                             ("nt", pathlib.PureWindowsPath("C:/generated/android"), "C:\\generated\\android\\gradlew.bat")):
            with self.subTest(platform=platform), patch.object(MODULE.os, "name", platform):
                command = MODULE.gradle_command(android)
                self.assertEqual(launcher, command[0])
                self.assertEqual(required, [x for x in command if x.startswith(":app:")])
                self.assertIn("--no-daemon", command)
                self.assertIn("--console=plain", command)


if __name__ == "__main__":
    unittest.main()
