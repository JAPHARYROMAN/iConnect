import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest


TOOLS = pathlib.Path(__file__).resolve().parents[2] / "tools"
POWERSHELL = shutil.which("pwsh")
if os.environ.get("CI") == "true" and not POWERSHELL:
    raise RuntimeError("CI must provide PowerShell 7 to execute the native patch guards")


@unittest.skipUnless(POWERSHELL, "PowerShell 7 is required for the native patch guards")
class NativePatchEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="iconnect-native-patch-")
        self.root = pathlib.Path(self.temporary.name).resolve()
        self.assertEqual(self.root.parent, pathlib.Path(tempfile.gettempdir()).resolve())
        self.addCleanup(self.temporary.cleanup)
        for name in ("native-transport-patches.ps1", "native-sctp-initialization.json", "native-dtls-initialization.json"):
            shutil.copyfile(TOOLS / name, self.root / name)
        (self.root / "native-patches").mkdir()
        shutil.copyfile(TOOLS / "native-patches/serialize-sctp-initialization.patch",
                        self.root / "native-patches/serialize-sctp-initialization.patch")
        shutil.copyfile(TOOLS / "native-patches/initialize-dtls-before-receive.patch",
                        self.root / "native-patches/initialize-dtls-before-receive.patch")
        self.definition = json.loads((self.root / "native-sctp-initialization.json").read_text(encoding="utf-8-sig"))
        self.dtls = json.loads((self.root / "native-dtls-initialization.json").read_text(encoding="utf-8-sig"))
        self.artifact = self.root / "artifact"
        self.artifact.mkdir()
        self.driver = self.root / "check.ps1"
        self.driver.write_text("""$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'native-transport-patches.ps1')
    $inputRecord = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'input.json') -Raw | ConvertFrom-Json -AsHashtable
    $patches = @(Get-IConnectNativeSourcePatches $inputRecord.evidence)
    if ($inputRecord.artifacts) { Assert-IConnectNativePatchArtifacts (Join-Path $PSScriptRoot 'artifact') $patches }
    @{count=$patches.Count} | ConvertTo-Json -Compress
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 17 }
""", encoding="utf-8")

    def check(self, evidence, artifacts=False):
        (self.root / "input.json").write_text(json.dumps({"evidence": evidence, "artifacts": artifacts}), encoding="utf-8")
        return subprocess.run([POWERSHELL, "-NoProfile", "-File", str(self.driver)],
                              capture_output=True, text=True, timeout=30, check=False)

    def reject(self, result, reason):
        self.assertEqual(result.returncode, 17, result.stdout + result.stderr)
        self.assertIn(reason, result.stderr)

    def artifact_inputs(self, compound=False):
        shutil.copyfile(self.root / "native-sctp-initialization.json", self.artifact / "native-sctp-initialization.json")
        (self.artifact / "native-patches").mkdir()
        shutil.copyfile(self.root / self.definition["patchFile"], self.artifact / self.definition["patchFile"])
        if compound:
            shutil.copyfile(self.root / "native-dtls-initialization.json", self.artifact / "native-dtls-initialization.json")
            shutil.copyfile(self.root / self.dtls["patchFile"], self.artifact / self.dtls["patchFile"])

    def test_legacy_unpatched_evidence_still_requires_no_patch(self):
        result = self.check({"stage": "BuildOnly"})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["count"], 0)

    def test_empty_patch_inventory_is_unpatched(self):
        result = self.check({"nativePatches": []})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["count"], 0)

    def test_exact_pinned_patch_and_artifact_inputs_are_accepted(self):
        self.artifact_inputs()
        result = self.check({"nativePatches": [self.definition]}, artifacts=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["count"], 1)

    def test_duplicate_patch_inventory_is_refused(self):
        self.reject(self.check({"nativePatches": [self.definition, self.definition]}), "Unsupported native patch inventory")

    def test_unknown_patch_is_refused(self):
        self.definition["id"] = "unreviewed-patch"
        self.reject(self.check({"nativePatches": [self.definition]}), "differs from the pinned correction")

    def test_wrong_source_revision_is_refused(self):
        self.definition["baseCommit"] = "0" * 40
        self.reject(self.check({"nativePatches": [self.definition]}), "differs from the pinned correction")

    def test_changed_postimage_is_refused(self):
        self.definition["postImages"]["src/impl/peerconnection.cpp"] = "0" * 64
        self.reject(self.check({"nativePatches": [self.definition]}), "patched source hashes differ")

    def test_unlisted_postimage_is_refused(self):
        self.definition["postImages"]["src/unlisted.cpp"] = "0" * 64
        self.reject(self.check({"nativePatches": [self.definition]}), "patched source hashes are incomplete")

    def test_forged_approval_field_is_refused(self):
        self.definition["productionApproved"] = True
        self.reject(self.check({"nativePatches": [self.definition]}), "unexpected fields")

    def test_changed_recipe_patch_is_refused(self):
        with (self.root / self.definition["patchFile"]).open("ab") as stream:
            stream.write(b"\nchanged input\n")
        self.reject(self.check({"nativePatches": [self.definition]}), "patch bytes differ")

    def test_changed_artifact_patch_is_refused(self):
        self.artifact_inputs()
        with (self.artifact / self.definition["patchFile"]).open("ab") as stream:
            stream.write(b"\nchanged artifact\n")
        self.reject(self.check({"nativePatches": [self.definition]}, artifacts=True), "exact patch inputs")

    def test_changed_artifact_definition_is_refused(self):
        self.artifact_inputs()
        (self.artifact / "native-sctp-initialization.json").write_text("{}", encoding="utf-8")
        self.reject(self.check({"nativePatches": [self.definition]}, artifacts=True), "exact patch inputs")

    def test_both_pinned_corrections_and_artifacts_are_accepted(self):
        self.artifact_inputs(compound=True)
        result = self.check({"nativePatches": [self.definition, self.dtls]}, artifacts=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["count"], 2)

    def test_reordered_corrections_are_refused(self):
        self.reject(self.check({"nativePatches": [self.dtls, self.definition]}), "inventory or order")

    def test_dtls_without_sctp_correction_is_refused(self):
        self.reject(self.check({"nativePatches": [self.dtls]}), "requires the SCTP correction")

    def test_altered_dtls_postimage_is_refused(self):
        self.dtls["postImages"]["src/impl/dtlstransport.cpp"] = "0" * 64
        self.reject(self.check({"nativePatches": [self.definition, self.dtls]}), "source hashes differ")

    def test_unlisted_dtls_source_path_is_refused(self):
        self.dtls["postImages"] = {"src/impl/peerconnection.cpp": "0" * 64}
        self.reject(self.check({"nativePatches": [self.definition, self.dtls]}), "source hashes differ")

    def test_changed_dtls_recipe_patch_is_refused(self):
        with (self.root / self.dtls["patchFile"]).open("ab") as stream:
            stream.write(b"\nchanged DTLS input\n")
        self.reject(self.check({"nativePatches": [self.definition, self.dtls]}), "patch bytes differ")

    def test_changed_dtls_artifact_is_refused(self):
        self.artifact_inputs(compound=True)
        with (self.artifact / self.dtls["patchFile"]).open("ab") as stream:
            stream.write(b"\nchanged DTLS artifact\n")
        self.reject(self.check({"nativePatches": [self.definition, self.dtls]}, artifacts=True), "exact patch inputs")

    def test_changed_dtls_artifact_definition_is_refused(self):
        self.artifact_inputs(compound=True)
        (self.artifact / "native-dtls-initialization.json").write_text("{}", encoding="utf-8")
        self.reject(self.check({"nativePatches": [self.definition, self.dtls]}, artifacts=True), "exact patch inputs")

    def test_unlisted_third_correction_is_refused(self):
        self.reject(self.check({"nativePatches": [self.definition, self.dtls, self.dtls]}), "Unsupported native patch inventory")


if __name__ == "__main__":
    unittest.main()
