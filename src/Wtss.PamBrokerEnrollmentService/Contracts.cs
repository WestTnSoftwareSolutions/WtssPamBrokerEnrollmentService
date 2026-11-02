using System.Text.Json.Serialization;

namespace Wtss.PamBrokerEnrollmentService;

public sealed record SafeguardSnapshot(IReadOnlyList<SafeguardAccount> Accounts, string AccessToken);

public sealed class SafeguardAccount
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? DomainName { get; init; }
    public bool HasPassword { get; init; }
    public bool HasSshKey { get; init; }
    public bool Disabled { get; init; }
    public SafeguardAsset Asset { get; init; } = new();
}

public sealed class SafeguardAsset
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? NetworkAddress { get; init; }
}

internal sealed class SafeguardAccountGroup
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public SafeguardAccount[] Accounts { get; init; } = [];
}

public sealed class BrokerRetrievableAccount
{
    public int AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string? AltAccountName { get; init; }
    public string? ApiKey { get; init; }
    public int AssetId { get; init; }
    public string AssetName { get; init; } = string.Empty;
    public string? NetworkAddress { get; init; }
    public string? DomainName { get; init; }
    public string[] IpRestrictions { get; init; } = [];
}

public sealed class PluginAccountMapping
{
    public int AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public int AssetId { get; init; }
    public string AssetName { get; init; } = string.Empty;
    public string? VaultName { get; init; }
}

public sealed class FullMonitorState
{
    public bool Enabled { get; set; }
    public ReverseFlowMonitorState ReverseFlowMonitorState { get; set; } = new();
    public string? StatusMessage { get; set; }
}

public sealed class ReverseFlowMonitorState
{
    public bool Enabled { get; set; }
    public int ReverseFlowPollingInterval { get; set; }
}

public sealed record BrokerInventory(
    IReadOnlyList<SafeguardAccount> AvailableAccounts,
    IReadOnlyList<BrokerRetrievableAccount> RetrievableAccounts,
    IReadOnlyList<PluginAccountMapping> PluginMappings,
    FullMonitorState MonitorState);

public interface ISafeguardInventoryClient
{
    Task<SafeguardSnapshot> GetAccountGroupSnapshotAsync(CancellationToken cancellationToken);
}

public interface ISecretsBrokerClient
{
    Task<ISecretsBrokerSession> OpenSessionAsync(string accessToken, CancellationToken cancellationToken);
}

public interface ISecretsBrokerSession : IAsyncDisposable
{
    Task<BrokerInventory> GetInventoryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BrokerRetrievableAccount>> EnsureRetrievableAsync(
        IReadOnlyList<SafeguardAccount> accounts,
        CancellationToken cancellationToken);
    Task SavePluginMappingsAsync(
        IReadOnlyList<BrokerRetrievableAccount> accounts,
        CancellationToken cancellationToken);
    Task<bool> RefreshMonitorAsync(CancellationToken cancellationToken);
}
