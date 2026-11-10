[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ConfigurationPath,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'Wtss.PamBrokerEnrollment.psm1'
Import-Module $modulePath -Force

$resolvedConfigurationPath = (Resolve-Path -LiteralPath $ConfigurationPath).Path
$configuration = Import-PowerShellDataFile -LiteralPath $resolvedConfigurationPath
$logPath = if ($configuration.LogPath) {
    [Environment]::ExpandEnvironmentVariables([string]$configuration.LogPath)
} else {
    Join-Path $PSScriptRoot 'logs\enrollment.jsonl'
}

$logDirectory = Split-Path -Parent $logPath
if ($logDirectory) {
    $null = New-Item -ItemType Directory -Path $logDirectory -Force
}

try {
    $result = Invoke-WtssPamBrokerEnrollment -Configuration $configuration -WhatIf:$WhatIf
    $entry = [ordered]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Level        = 'Information'
        Result       = $result
    }
    Add-Content -LiteralPath $logPath -Value ($entry | ConvertTo-Json -Depth 6 -Compress) -Encoding utf8
    $result
    exit 0
}
catch {
    $entry = [ordered]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Level        = 'Error'
        Error        = $_.Exception.Message
    }
    Add-Content -LiteralPath $logPath -Value ($entry | ConvertTo-Json -Depth 4 -Compress) -Encoding utf8
    Write-Error $_
    exit 1
}
