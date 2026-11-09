# Native connection acceptance on separate networks

This focused check follows the Windows SCTP/DTLS startup corrections. It is prepared for the operator and has **not been executed on a physical phone**. Use it alongside the complete [physical-network procedure](physical-network-validation.md), after the intended builds have passed their package and signing review.

## Bind the run to its builds

Record the Git commit, both app versions, Windows version, Android version, package SHA-256 values and UTC date. For the Windows installation or unpackaged publish directory used in the run, record the runtime DLL and the source-bundle identity:

```powershell
# Set this to the actual build under test, including all its runtime files.
$windowsDirectory = 'C:\path\to\reviewed\iConnect'
Get-FileHash (Join-Path $windowsDirectory 'libDatachannel/datachannel.dll') -Algorithm SHA256
./tools/assert-native-transport.ps1 `
    -BundleDirectory (Join-Path $windowsDirectory 'PackageMetadata/NativeTransport')
```

The runtime hash must match the verified bundle's binary hash, both ordered correction IDs must be present, and application selection must accept the current supported source lock. `016d8b44…` identifies the local OpenSSL 3.5.9 LTS build under qualification on 9 November. The earlier `57f119d4…` binary uses the unsupported 3.6 line and is retained only as historical evidence. A separately compiled hosted build may have a different binary hash and must retain its own build/source evidence. See [the LTS baseline and remaining qualification](native-openssl-lts.md). Hash verification alone does not establish a trusted production signature.

Use a reachable HTTPS coordinator with its normal certificate validation and a deliberately selected STUN service. Keep Windows on one internet connection and the physical phone on mobile data with Wi-Fi off. Record both connection types and which device initiates each attempt. The app uses direct connections and has no relay fallback, so an unreachable network pair must be reported as a failed attempt.

## Execute and record

1. Prepare generated files with `./tools/field-network-test.ps1 -Mode Prepare`. Keep `expected.json` outside the approved `share` folder. Use fresh destination folders and the default small payload for this first check.
2. Pair through the normal invitation and code-comparison flow. Verify that pairing has created no storage approval. Approve only the generated source/destination folders needed for the transfers.
3. Initiate five connection attempts from Windows and five from Android, disconnecting normally between attempts. Record every attempt in order, including the first failure. A successful attempt must reach authenticated readiness on both devices and deliver a small verified file; a discovered network path alone is insufficient.
4. On a ready session, copy the generated fixture in both directions and run the existing `field-network-test.ps1 -Mode Verify` checks against the appropriate received folder. Preserve source files and collision originals.
5. Disconnect, close and reopen the apps, then connect again using their saved pairing. Confirm that readiness and verified file delivery still succeed. Finally revoke the pairing and verify that the old peer cannot reconnect or browse an earlier approval.

Use a report row for each attempt:

| Attempt | UTC start | Initiator | Network pair | Ready on both devices | Verified file | Outcome / failure text |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Pending | Windows | Pending | Not run | Not run | Not run |

Record first-failure evidence before retrying. Retain the app's displayed error and any explicitly available numeric connection/ICE history. A completed ICE path followed by a failed secure session needs separate transport/authentication investigation; it should not automatically be labeled an ICE failure. Do not export SDP, candidate addresses, invitations, verification codes or device keys. If the UI cannot identify the failing stage, record it as unknown.

Any failed readiness attempt, missing hash verification or lost revocation refusal leaves this acceptance check incomplete. Keep a restrictive-network failure separate from evidence on a reachable network pair. Ten local attempts, even if all pass, do not establish a general reliability rate or complete IPv4/IPv6, provider, power-loss, accessibility, installed-lifecycle or independent security coverage.
