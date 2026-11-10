Set-StrictMode -Version Latest

function Get-AccountId {
    param([Parameter(Mandatory)]$Account)

    if ($null -ne $Account.PSObject.Properties['AccountId']) {
        return [int]$Account.AccountId
    }

    if ($null -ne $Account.PSObject.Properties['Id']) {
        return [int]$Account.Id
    }

    throw 'An account object did not contain Id or AccountId.'
}

function Get-WtssEnrollmentPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$GroupAccounts,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$AvailableAccounts,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$MappedAccounts
    )

    $availableById = @{}
    foreach ($account in $AvailableAccounts) {
        $availableById[(Get-AccountId $account)] = $account
    }

    $mappedIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($account in $MappedAccounts) {
        [void]$mappedIds.Add((Get-AccountId $account))
    }

    $eligible = foreach ($account in $GroupAccounts) {
        $hasPassword = $null -ne $account.PSObject.Properties['HasPassword'] -and [bool]$account.HasPassword
        $disabled = $null -ne $account.PSObject.Properties['Disabled'] -and [bool]$account.Disabled
        $accountId = Get-AccountId $account

        if ($hasPassword -and -not $disabled -and $availableById.ContainsKey($accountId)) {
            $availableById[$accountId]
        }
    }

    $eligible = @($eligible | Sort-Object { Get-AccountId $_ } -Unique)
    $missing = @($eligible | Where-Object { -not $mappedIds.Contains((Get-AccountId $_)) })
    $eligibleIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($account in $eligible) { [void]$eligibleIds.Add((Get-AccountId $account)) }
    $mappedOutsideSelection = @($MappedAccounts | Where-Object { -not $eligibleIds.Contains((Get-AccountId $_)) })

    [pscustomobject]@{
        EligibleAccounts       = $eligible
        MissingAccounts        = $missing
        MappedOutsideSelection = $mappedOutsideSelection
        EligibleCount          = $eligible.Count
        MissingCount           = $missing.Count
        ReviewCount            = $mappedOutsideSelection.Count
    }
}

function Assert-WtssEnrollmentConfiguration {
    param([Parameter(Mandatory)][hashtable]$Configuration)

    $required = @(
        'SafeguardAddress',
        'SafeguardCertificateThumbprint',
        'SecretsBrokerAddress',
        'AccountGroup',
        'PluginInstanceName'
    )

    foreach ($name in $required) {
        if (-not $Configuration.ContainsKey($name) -or [string]::IsNullOrWhiteSpace([string]$Configuration[$name])) {
            throw "Configuration value '$name' is required."
        }
    }

    if ($Configuration.ContainsKey('SecretsBrokerPort') -and
        ([int]$Configuration.SecretsBrokerPort -lt 1 -or [int]$Configuration.SecretsBrokerPort -gt 65535)) {
        throw 'SecretsBrokerPort must be between 1 and 65535.'
    }
}

function Invoke-WtssPamBrokerEnrollment {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][hashtable]$Configuration
    )

    Assert-WtssEnrollmentConfiguration $Configuration

    $mutexName = if ($Configuration.ContainsKey('MutexName') -and $Configuration['MutexName']) {
        [string]$Configuration['MutexName']
    } else {
        'Local\WtssPamBrokerEnrollment'
    }
    $mutex = [System.Threading.Mutex]::new($false, $mutexName)
    $lockTaken = $false
    $safeguardConnected = $false
    $brokerConnected = $false

    try {
        $lockTaken = $mutex.WaitOne(0)
        if (-not $lockTaken) {
            throw 'Another enrollment reconciliation is already running.'
        }

        Import-Module safeguard-ps -MinimumVersion 8.0 -ErrorAction Stop
        Import-Module safeguard-devops -ErrorAction Stop

        $safeguardApiVersion = if ($Configuration.ContainsKey('SafeguardApiVersion')) {
            [int]$Configuration['SafeguardApiVersion']
        } else { 4 }
        $serviceApiVersion = if ($Configuration.ContainsKey('SecretsBrokerApiVersion')) {
            [int]$Configuration['SecretsBrokerApiVersion']
        } else { 2 }
        $insecure = $Configuration.ContainsKey('IgnoreSslErrors') -and $Configuration['IgnoreSslErrors'] -eq $true

        Connect-Safeguard `
            -Appliance ([string]$Configuration.SafeguardAddress) `
            -Thumbprint ([string]$Configuration.SafeguardCertificateThumbprint) `
            -Version $safeguardApiVersion `
            -Insecure:$insecure `
            -NoWindowTitle `
            -ErrorAction Stop
        $safeguardConnected = $true

        $connectBroker = @{
            ServiceAddress   = [string]$Configuration.SecretsBrokerAddress
            ServiceApiVersion = $serviceApiVersion
            Insecure         = $insecure
            ErrorAction      = 'Stop'
        }
        if ($Configuration.ContainsKey('SecretsBrokerPort') -and $Configuration['SecretsBrokerPort']) {
            $connectBroker.ServicePort = [int]$Configuration['SecretsBrokerPort']
        }
        Connect-SgDevOps @connectBroker
        $brokerConnected = $true

        $groupAccounts = @(Get-SafeguardAccountGroupMember -GroupToGet $Configuration.AccountGroup -ErrorAction Stop)
        $availableAccounts = @(Get-SgDevOpsAvailableAssetAccount -ErrorAction Stop)
        $mappedAccounts = @(Get-SgDevOpsMappedAssetAccount -PluginName $Configuration.PluginInstanceName -ErrorAction Stop)
        $plan = Get-WtssEnrollmentPlan `
            -GroupAccounts $groupAccounts `
            -AvailableAccounts $availableAccounts `
            -MappedAccounts $mappedAccounts

        if ($plan.MissingCount -eq 0) {
            return [pscustomobject]@{
                Status           = 'NoChange'
                EligibleCount    = $plan.EligibleCount
                AddedCount       = 0
                AddedAccountIds  = @()
                ReviewAccountIds = @($plan.MappedOutsideSelection | ForEach-Object { Get-AccountId $_ })
                MonitorRefreshed = $false
                Message          = 'All eligible accounts are already mapped.'
            }
        }

        $missingIds = @($plan.MissingAccounts | ForEach-Object { Get-AccountId $_ })
        if (-not $PSCmdlet.ShouldProcess(
                "plugin '$($Configuration.PluginInstanceName)'",
                "register and map $($plan.MissingCount) Safeguard account(s): $($missingIds -join ', ')")) {
            return [pscustomobject]@{
                Status           = 'WhatIf'
                EligibleCount    = $plan.EligibleCount
                AddedCount       = 0
                AddedAccountIds  = $missingIds
                ReviewAccountIds = @($plan.MappedOutsideSelection | ForEach-Object { Get-AccountId $_ })
                MonitorRefreshed = $false
                Message          = 'No changes were made.'
            }
        }

        $null = Register-SgDevOpsAssetAccount -AccountObjects $plan.MissingAccounts -ErrorAction Stop

        $registeredAccounts = @(Get-SgDevOpsRegisteredAssetAccount -ErrorAction Stop)
        $missingIdSet = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($accountId in $missingIds) { [void]$missingIdSet.Add($accountId) }
        $accountsToMap = @($registeredAccounts | Where-Object { $missingIdSet.Contains((Get-AccountId $_)) })
        if ($accountsToMap.Count -ne $missingIds.Count) {
            throw "Secrets Broker registered $($accountsToMap.Count) of $($missingIds.Count) requested accounts."
        }

        $null = Add-SgDevOpsMappedAssetAccount `
            -PluginName $Configuration.PluginInstanceName `
            -AccountObjects $accountsToMap `
            -ErrorAction Stop

        $verifiedMappings = @(Get-SgDevOpsMappedAssetAccount -PluginName $Configuration.PluginInstanceName -ErrorAction Stop)
        $verifiedIds = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($account in $verifiedMappings) { [void]$verifiedIds.Add((Get-AccountId $account)) }
        $unverifiedIds = @($missingIds | Where-Object { -not $verifiedIds.Contains($_) })
        if ($unverifiedIds.Count -gt 0) {
            throw "Plugin mapping verification failed for account IDs: $($unverifiedIds -join ', ')."
        }

        $monitor = Get-SgDevOpsMonitor -ErrorAction Stop
        $monitorRefreshed = $false
        $message = 'Accounts were registered and mapped; the disabled monitor was left unchanged.'
        if ([bool]$monitor.Enabled) {
            $reverseEnabled = $false
            $reverseInterval = 60
            if ($null -ne $monitor.PSObject.Properties['ReverseFlowMonitorState'] -and $monitor.ReverseFlowMonitorState) {
                $reverseEnabled = [bool]$monitor.ReverseFlowMonitorState.Enabled
                if ([int]$monitor.ReverseFlowMonitorState.ReverseFlowPollingInterval -gt 0) {
                    $reverseInterval = [int]$monitor.ReverseFlowMonitorState.ReverseFlowPollingInterval
                }
            }

            $null = Set-SgDevOpsMonitor $false $reverseEnabled $reverseInterval -ErrorAction Stop
            $null = Set-SgDevOpsMonitor $true $reverseEnabled $reverseInterval -ErrorAction Stop
            $monitorRefreshed = $true
            $message = 'Accounts were registered and mapped; the monitor was refreshed to request the initial credential pull.'
        }

        [pscustomobject]@{
            Status           = 'Changed'
            EligibleCount    = $plan.EligibleCount
            AddedCount       = $missingIds.Count
            AddedAccountIds  = $missingIds
            ReviewAccountIds = @($plan.MappedOutsideSelection | ForEach-Object { Get-AccountId $_ })
            MonitorRefreshed = $monitorRefreshed
            Message          = $message
        }
    }
    finally {
        if ($brokerConnected) {
            try { Disconnect-SgDevOps -ErrorAction Stop } catch { Write-Warning "Secrets Broker logoff failed: $($_.Exception.Message)" }
        }
        if ($safeguardConnected) {
            try { Disconnect-Safeguard -ErrorAction Stop } catch { Write-Warning "Safeguard logoff failed: $($_.Exception.Message)" }
        }
        if ($lockTaken) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

Export-ModuleMember -Function Get-WtssEnrollmentPlan, Invoke-WtssPamBrokerEnrollment
