# WTSS PAM Broker Enrollment Service

An add-only enrollment controller for One Identity Safeguard for Privileged Passwords (SPP) and Safeguard Secrets Broker. It detects password-bearing accounts in one configured SPP account group, adds eligible accounts to the Broker's A2A registration and one plugin instance, and refreshes an already-enabled Broker monitor so newly mapped credentials are pushed immediately.

Two deployment forms are maintained in this repository:

- `powershell/` is the recommended client MVP: an idempotent reconciliation script run by Windows Task Scheduler using the supported `safeguard-ps` and `safeguard-devops` modules.
- `src/` is the stateful .NET service and remains the foundation for a future productized or unified extension-server component.

See [`powershell/README.md`](powershell/README.md) for the PowerShell deployment, security model, dry run, and Scheduled Task installation.

This is the standalone Part 1 implementation. It intentionally does not remove mappings, alter the selected SPP group, or directly mutate SPP's A2A registration. It uses the same Broker API sequence as the Secrets Broker UI, allowing the Broker to own its SPP-side A2A configuration.

## Reconciliation flow

1. Authenticate to SPP with a certificate user and read the configured account group.
2. Authenticate to Secrets Broker using the short-lived SPP bearer token.
3. Intersect enabled, password-bearing group accounts with `Safeguard/AvailableAccounts`.
4. Exclude accounts already mapped to the configured plugin.
5. Wait for the configurable quiet period (bounded by maximum batch age).
6. `PUT Safeguard/A2ARegistration/RetrievableAccounts`.
7. `PUT Plugins/{plugin}/Accounts` with the returned A2A entries.
8. If the monitor was enabled, preserve reverse-flow settings while cycling the monitor off/on. Monitor startup performs the Broker's initial password pull.
9. Run a settlement pass after the configured delay. Accounts added during the first pass are picked up without being lost.

Existing manual mappings are preserved. Accounts previously enrolled by this service that leave the selected group are reported in status/logs but remain mapped in this add-only release.

## Configuration

Copy `src/Wtss.PamBrokerEnrollmentService/appsettings.json` to `appsettings.Production.json` beside the deployed executable, or override values with standard .NET configuration environment variables such as `Enrollment__AccountGroupId`.

Required settings:

| Setting | Purpose |
| --- | --- |
| `SafeguardAddress` | SPP appliance hostname/address accepted by SafeguardDotNet |
| `SafeguardCertificatePath` | PFX/PKCS#12 certificate-user file readable by the service identity |
| `SecretsBrokerBaseUri` | HTTPS base URI of Secrets Broker |
| `AccountGroupId` | SPP account group used as the enrollment selector |
| `PluginInstanceName` | Exact configured Bitwarden (or other password plugin) instance name |

The certificate passphrase is read from the environment variable named by `SafeguardCertificatePassphraseEnvironmentVariable`; it is never stored in the state database. The certificate user must be able to read the account group and must have the SPP role required by Secrets Broker administrative logon (currently Policy Administrator).

TLS validation is enabled by default. `IgnoreSslErrors=true` exists for non-production validation only.

## Run and inspect

```powershell
dotnet run --project .\src\Wtss.PamBrokerEnrollmentService
```

The local management endpoint listens on `http://127.0.0.1:5178` by default:

- `GET /health/live`
- `GET /health/ready`
- `GET /api/status`
- `POST /api/reconcile` (immediate, coalesced reconciliation request)

SQLite state is stored at `data/enrollment-state.db` by default. It contains account IDs, display metadata, ownership state, and timestamps—never credentials, access tokens, or plugin API keys.

## Build and test

```powershell
dotnet restore .\WtssPamBrokerEnrollmentService.sln
dotnet build .\WtssPamBrokerEnrollmentService.sln --no-restore
dotnet test .\WtssPamBrokerEnrollmentService.sln --no-build --no-restore
```

## Versioning and prerelease artifacts

The repository-root `VERSION` file is the source of truth for the application and release version. Versions use semantic versioning. Until the service is declared stable, every published version must include a prerelease suffix, for example:

```text
0.1.0-preview.1
0.1.0-preview.2
0.1.0-rc.1
```

`Directory.Build.props` applies this version to all projects during local and CI builds.

To publish a test build:

1. Increment `VERSION` and commit the change.
2. Push the commit to GitHub.
3. Open **Actions**, select **Publish prerelease**, and choose **Run workflow** for that commit or branch.
4. Download `WtssPamBrokerEnrollmentService-<version>-win-x64.zip` from the resulting GitHub prerelease.
5. Verify the adjacent `.sha256` file before transferring the ZIP into the client environment.

For the Scheduled Task deployment, download `WtssPamBrokerEnrollmentPowerShell-<version>.zip` and verify its adjacent checksum instead.

The workflow restores, builds, and tests the solution and runs the Pester suite before publishing. It produces both a self-contained Windows x64 service package and a separate PowerShell package, uploads short-lived workflow artifacts, and creates an immutable `v<version>` GitHub prerelease. A version cannot be published again after its tag exists; increment `VERSION` for every new build.

## First-pass boundaries

- One SPP appliance, one Secrets Broker, one account group, and one plugin instance.
- Password-capable accounts only.
- Add-only lifecycle. Offboarding is detected and reported, not executed.
- Polling plus manual trigger; no SignalR dependency.
- Monitor refresh only when it was already enabled; the service never silently enables it.
- Localhost-only management API without remote authentication.
