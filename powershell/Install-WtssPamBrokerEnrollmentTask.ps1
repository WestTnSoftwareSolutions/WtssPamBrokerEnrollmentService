[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ConfigurationPath,

    [Parameter(Mandatory)]
    [pscredential]$Credential,

    [string]$TaskName = 'WTSS PAM Broker Enrollment',

    [ValidateRange(1, 1440)]
    [int]$IntervalMinutes = 5
)

$ErrorActionPreference = 'Stop'
$entryScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Invoke-WtssPamBrokerEnrollment.ps1')).Path
$configuration = (Resolve-Path -LiteralPath $ConfigurationPath).Path
$powerShell = (Get-Command pwsh.exe -ErrorAction Stop).Source
$arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy AllSigned -File `"$entryScript`" -ConfigurationPath `"$configuration`""

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes ([Math]::Max(10, $IntervalMinutes * 2)))

if ($PSCmdlet.ShouldProcess($TaskName, "register Scheduled Task as $($Credential.UserName)")) {
    $plainPassword = $Credential.GetNetworkCredential().Password
    try {
        Register-ScheduledTask `
            -TaskName $TaskName `
            -Action $action `
            -Trigger $trigger `
            -Settings $settings `
            -User $Credential.UserName `
            -Password $plainPassword `
            -Description 'Add-only reconciliation from an SPP account group to a Secrets Broker plugin.' `
            -Force | Out-Null
    }
    finally {
        $plainPassword = $null
    }

    Get-ScheduledTask -TaskName $TaskName
}
