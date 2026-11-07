# Self-host the iConnect coordinator

The coordinator helps devices pair and find each other over HTTPS WebSockets. The clients authenticate each other and exchange files on their direct connection. Run one coordinator instance per origin because registrations, pending invitations and presence are held in memory. Each client uses the same coordinator origin and an optional STUN endpoint for direct reachability.

## Build a release

From the repository root on Windows, use the installed .NET 10 SDK to publish a framework-dependent release to a fresh directory. This command restores against the checked-in dependency locks.

```powershell
./tools/dev.ps1 publish-coordinator --output .local/releases/coordinator
```

Copy the entire output directory to the host. The Linux template expects `/usr/bin/dotnet`, the ASP.NET Core 10 runtime, and systemd with `LoadCredential=` support. The runtime and system packages need regular security updates. [ASP.NET Core runtime support](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [systemd service credentials](https://systemd.io/CREDENTIALS/)

## Configure the host

1. Create a dedicated `iconnect-rendezvous` system user and group with no interactive login. Install the publish output in `/opt/iconnect-rendezvous`. Give the service user read and execute access where needed. Keep the directory and application files owned by the host administrator.
2. Point a DNS name you control at the host. Obtain a certificate and full chain trusted by both Windows and Android for that name. Put the full chain in `/etc/iconnect-rendezvous/certificate.pem` and its matching PEM private key in `/etc/iconnect-rendezvous/private-key.pem`. Keep both root-owned and set the key to mode `0600`. The service receives private copies through systemd credentials. [Kestrel HTTPS certificate configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0), [systemd service credentials](https://systemd.io/CREDENTIALS/)
3. Create the root-owned `/etc/iconnect-rendezvous/host.env` with `AllowedHosts=coordinator.example.org`, replacing the example with your DNS name. Use the hostname alone. The template binds IPv4 on TCP `8443`. Allow that port on the host and upstream firewalls. Use DNS records that lead to an active listener. Kestrel terminates TLS on the coordinator host. Use direct TCP routing and keep forwarded-header processing disabled.
4. Install [the unit template](../../backend/IConnect.Rendezvous/deploy/iconnect-rendezvous.service) as `/etc/systemd/system/iconnect-rendezvous.service`. Confirm the binary and credential paths for your host. Verify the unit, then start it as the host administrator.

   ```sh
   sudo systemd-analyze verify /etc/systemd/system/iconnect-rendezvous.service
   sudo systemctl daemon-reload
   sudo systemctl enable --now iconnect-rendezvous.service
   sudo systemctl status iconnect-rendezvous.service
   ```

5. From a different network, check certificate trust, hostname, status and headers using normal certificate verification.

   ```sh
   curl --fail --silent --show-error --include https://coordinator.example.org:8443/health
   ```

   Expect HTTP `200`, `Cache-Control: no-store` and JSON with `service` equal to `iConnect rendezvous`, `protocol` equal to `1` and `fileRelay` equal to `false`. Enter `https://coordinator.example.org:8443` in both clients. The Android phone on cellular and the Windows PC on a separate network can then follow the [physical network test](physical-network-validation.md).

## Operate the coordinator

- The instance limits concurrent HTTP connections to 128, upgraded signalling sockets to 80 including unregistered sockets, and registered devices to 64. The signalling route allows 20 attempts per source IP per minute, which can affect users sharing an internet address. A registered socket can send 240 messages per minute. Registration has 10 seconds and each socket has a one-hour lifetime.
- Monitor public `/health` availability and certificate expiry from outside the host. `/health` reports process readiness for signalling, and an actual pairing and direct connection checks the full path. During a restart the listener may close before a probe can receive HTTP `503`. Keep service logs access-controlled. Routine signalling failures are logged at debug level with a fixed exception category.
- Stage renewed certificate and key files together and restart the service to load them. Check external health and reconnect both clients after rotation or upgrade. Restart clears in-memory coordinator state. Existing pairing trust is stored on the clients. Recreate any pending invitation after restart.

The Release build and portable publish passed. All 24 focused tests and 848 selected Windows tests passed. The selected run excluded three 4,096-item capacity cases. Production configuration and signalling admission are covered by in-process fixtures. Verify the unit on your Linux host and record the deployed TLS and separate-network results before distributing the app.
