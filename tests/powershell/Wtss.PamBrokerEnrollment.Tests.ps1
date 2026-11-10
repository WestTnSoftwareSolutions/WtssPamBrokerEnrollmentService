BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\..\powershell\Wtss.PamBrokerEnrollment.psm1') -Force
}

BeforeAll {
    function New-TestAccount {
        param(
            [int]$Id,
            [bool]$HasPassword = $true,
            [bool]$Disabled = $false
        )

        [pscustomobject]@{
            Id          = $Id
            Name        = "account-$Id"
            HasPassword = $HasPassword
            Disabled    = $Disabled
        }
    }
}

Describe 'Get-WtssEnrollmentPlan' {
    It 'selects enabled password accounts available to Broker and not already mapped' {
        $group = @(
            (New-TestAccount 1),
            (New-TestAccount 2 -HasPassword $false),
            (New-TestAccount 3 -Disabled $true),
            (New-TestAccount 4),
            (New-TestAccount 5)
        )
        $available = @(
            (New-TestAccount 1),
            (New-TestAccount 2),
            (New-TestAccount 3),
            (New-TestAccount 4)
        )
        $mapped = @([pscustomobject]@{ AccountId = 4 })

        $plan = Get-WtssEnrollmentPlan -GroupAccounts $group -AvailableAccounts $available -MappedAccounts $mapped

        $plan.EligibleCount | Should -Be 2
        $plan.MissingCount | Should -Be 1
        $plan.MissingAccounts.Id | Should -Be 1
        $plan.ReviewCount | Should -Be 0
    }

    It 'returns no missing accounts when all eligible accounts are mapped' {
        $account = New-TestAccount 7

        $plan = Get-WtssEnrollmentPlan `
            -GroupAccounts @($account) `
            -AvailableAccounts @($account) `
            -MappedAccounts @([pscustomobject]@{ AccountId = 7 })

        $plan.MissingCount | Should -Be 0
        $plan.ReviewCount | Should -Be 0
    }

    It 'deduplicates group members by account ID' {
        $account = New-TestAccount 9

        $plan = Get-WtssEnrollmentPlan `
            -GroupAccounts @($account, $account) `
            -AvailableAccounts @($account) `
            -MappedAccounts @()

        $plan.EligibleCount | Should -Be 1
        $plan.MissingCount | Should -Be 1
    }

    It 'reports but does not remove mapped accounts outside the current selection' {
        $eligible = New-TestAccount 10

        $plan = Get-WtssEnrollmentPlan `
            -GroupAccounts @($eligible) `
            -AvailableAccounts @($eligible) `
            -MappedAccounts @([pscustomobject]@{ AccountId = 11 })

        $plan.MissingCount | Should -Be 1
        $plan.ReviewCount | Should -Be 1
        $plan.MappedOutsideSelection.AccountId | Should -Be 11
    }
}

Describe 'Invoke-WtssPamBrokerEnrollment' {
    InModuleScope Wtss.PamBrokerEnrollment {
        BeforeAll {
            function Connect-Safeguard {}
            function Disconnect-Safeguard {}
            function Connect-SgDevOps {}
            function Disconnect-SgDevOps {}
            function Get-SafeguardAccountGroupMember {}
            function Get-SgDevOpsAvailableAssetAccount {}
            function Get-SgDevOpsMappedAssetAccount {}
            function Register-SgDevOpsAssetAccount {}
            function Get-SgDevOpsRegisteredAssetAccount {}
            function Add-SgDevOpsMappedAssetAccount {}
            function Get-SgDevOpsMonitor {}
            function Set-SgDevOpsMonitor {
                param(
                    [bool]$EnableA2a,
                    [bool]$EnableReverseFlow,
                    [int]$PollIntervalReverseFlow
                )
            }
        }

        BeforeEach {
            Mock Import-Module {}
            Mock Connect-Safeguard {}
            Mock Disconnect-Safeguard {}
            Mock Connect-SgDevOps {}
            Mock Disconnect-SgDevOps {}
        }

        It 'registers, maps, verifies, and refreshes an enabled monitor once per changed run' {
            $account = [pscustomobject]@{ Id = 21; Name = 'account-21'; HasPassword = $true; Disabled = $false }
            $registered = [pscustomobject]@{ AccountId = 21; AccountName = 'account-21' }
            $script:mappingReads = 0

            Mock Get-SafeguardAccountGroupMember { @($account) }
            Mock Get-SgDevOpsAvailableAssetAccount { @($account) }
            Mock Get-SgDevOpsMappedAssetAccount {
                $script:mappingReads++
                if ($script:mappingReads -eq 1) { @() } else { @($registered) }
            }
            Mock Register-SgDevOpsAssetAccount { @($registered) }
            Mock Get-SgDevOpsRegisteredAssetAccount { @($registered) }
            Mock Add-SgDevOpsMappedAssetAccount {}
            Mock Get-SgDevOpsMonitor {
                [pscustomobject]@{
                    Enabled = $true
                    ReverseFlowMonitorState = [pscustomobject]@{
                        Enabled = $true
                        ReverseFlowPollingInterval = 120
                    }
                }
            }
            Mock Set-SgDevOpsMonitor {}

            $result = Invoke-WtssPamBrokerEnrollment -Configuration @{
                SafeguardAddress = 'spp.example.com'
                SafeguardCertificateThumbprint = 'thumbprint'
                SecretsBrokerAddress = 'broker.example.com'
                AccountGroup = 1
                PluginInstanceName = 'Bitwarden'
                MutexName = "Local\WtssEnrollmentTest-$([guid]::NewGuid())"
            }

            $result.Status | Should -Be 'Changed'
            $result.AddedAccountIds | Should -Be 21
            $result.MonitorRefreshed | Should -BeTrue
            Should -Invoke Register-SgDevOpsAssetAccount -Times 1 -Exactly
            Should -Invoke Add-SgDevOpsMappedAssetAccount -Times 1 -Exactly
            Should -Invoke Set-SgDevOpsMonitor -Times 1 -Exactly -ParameterFilter { $EnableA2a -eq $false }
            Should -Invoke Set-SgDevOpsMonitor -Times 1 -Exactly -ParameterFilter { $EnableA2a -eq $true }
        }

        It 'does not mutate Broker or monitor state when no accounts are missing' {
            $account = [pscustomobject]@{ Id = 22; Name = 'account-22'; HasPassword = $true; Disabled = $false }
            $mapped = [pscustomobject]@{ AccountId = 22; AccountName = 'account-22' }
            Mock Get-SafeguardAccountGroupMember { @($account) }
            Mock Get-SgDevOpsAvailableAssetAccount { @($account) }
            Mock Get-SgDevOpsMappedAssetAccount { @($mapped) }
            Mock Register-SgDevOpsAssetAccount {}
            Mock Add-SgDevOpsMappedAssetAccount {}
            Mock Set-SgDevOpsMonitor {}

            $result = Invoke-WtssPamBrokerEnrollment -Configuration @{
                SafeguardAddress = 'spp.example.com'
                SafeguardCertificateThumbprint = 'thumbprint'
                SecretsBrokerAddress = 'broker.example.com'
                AccountGroup = 1
                PluginInstanceName = 'Bitwarden'
                MutexName = "Local\WtssEnrollmentTest-$([guid]::NewGuid())"
            }

            $result.Status | Should -Be 'NoChange'
            Should -Invoke Register-SgDevOpsAssetAccount -Times 0 -Exactly
            Should -Invoke Add-SgDevOpsMappedAssetAccount -Times 0 -Exactly
            Should -Invoke Set-SgDevOpsMonitor -Times 0 -Exactly
        }
    }
}
