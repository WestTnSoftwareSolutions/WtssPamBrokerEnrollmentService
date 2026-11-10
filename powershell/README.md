# PowerShell scheduled reconciliation

This is the client-oriented MVP deployment option. It performs the same add-only enrollment as the .NET service without installing a continuously running application:

1. Authenticate to SPP with a certificate from the Scheduled Task identity's `CurrentUser\My` certificate store.
2. Read enabled, password-bearing members of one SPP account group.
3. Intersect those accounts with the accounts available to Secrets Broker.
4. Register missing accounts with the Broker-managed A2A registration.
5. Map them to one plugin instance and verify the result.
6. If the monitor was already enabled, cycle only the A2A monitor state while preserving reverse-flow settings. This requests the initial credential pull.

The script never removes mappings, unregisters accounts, enables a disabled monitor, or stores credentials. Existing mapped accounts outside the current selection are emitted as `ReviewAccountIds`, not removed. Runs are idempotent and protected by both a named mutex and the Scheduled Task `IgnoreNew` setting.

## Prerequisites

- PowerShell 7.
- `safeguard-ps` and `safeguard-devops` module versions tested with the installed SPP and Secrets Broker releases.
- A dedicated Windows task identity with **Log on as a batch job**.
- Its SPP certificate-user certificate, including the private key, in that identity's `CurrentUser\My` store.
- Read access to the selected SPP account group and the SPP role required for Secrets Broker administrative logon and mutation. Do not assume Auditor alone is sufficient; the currently tested Broker workflow requires Policy Administrator.
- Trusted TLS certificates for SPP and Secrets Broker.

Run these checks in a PowerShell process launched as the task identity:

```powershell
Get-ChildItem Cert:\CurrentUser\My\<thumbprint> | Format-List Subject, Thumbprint, HasPrivateKey, NotAfter
Get-Module -ListAvailable safeguard-ps, safeguard-devops | Select-Object Name, Version, Path
```

## Configure and test

Copy `enrollment.example.psd1` outside the repository, restrict its ACL, and update the appliance, Broker, group, plugin, and certificate values. The file contains no secret, but it is operationally sensitive.

First run a non-mutating reconciliation as the task identity:

```powershell
pwsh -NoProfile -File .\Invoke-WtssPamBrokerEnrollment.ps1 `
  -ConfigurationPath C:\ProgramData\WTSS\PamBrokerEnrollment\enrollment.psd1 `
  -WhatIf
```

Review the proposed account IDs and the JSON-lines log. Then run without `-WhatIf`. Confirm the accounts appear in the plugin and that each expected secret was pushed to Bitwarden.

## Install the Scheduled Task

Sign the operational `.ps1` files and the `.psm1` module with the client's trusted code-signing certificate before installation. From an elevated, interactive PowerShell session:

```powershell
$credential = Get-Credential 'DOMAIN\svc-pam-enrollment'
.\Install-WtssPamBrokerEnrollmentTask.ps1 `
  -ConfigurationPath C:\ProgramData\WTSS\PamBrokerEnrollment\enrollment.psd1 `
  -Credential $credential `
  -IntervalMinutes 5
```

The installer uses `pwsh.exe`, `-NonInteractive`, `-NoProfile`, and `-ExecutionPolicy AllSigned`. It schedules the first run one minute later and rejects overlapping instances. The task password is passed only to the Windows Task Scheduler registration API and is not stored by these scripts.

Monitor the task's `LastTaskResult` and alert on nonzero values. The default log is `%ProgramData%\WTSS\PamBrokerEnrollment\enrollment.jsonl`; it contains status, account IDs, and error messages, never access tokens or credential values.

## Upgrade and rollback

Pin and test module versions before upgrading them. The `safeguard-devops` major/minor version should match the Secrets Broker release. To roll back, disable the Scheduled Task and restore the previous signed script directory. Existing Broker mappings are intentionally left intact.

## Boundary: no automatic offboarding

This release is add-only. Automatic removal needs persistent ownership state, a grace period, and a check that an A2A registration is not used by another plugin. Until those controls are implemented, accounts that leave the group should be reported and reviewed rather than automatically removed.
