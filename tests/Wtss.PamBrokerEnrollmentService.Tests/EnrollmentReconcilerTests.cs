using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wtss.PamBrokerEnrollmentService;

namespace Wtss.PamBrokerEnrollmentService.Tests;

public sealed class EnrollmentReconcilerTests
{
    [Fact]
    public async Task ManualReconcileEnrollsOnceAndThenBecomesIdempotent()
    {
        var account = Account(101, "sql-admin");
        var options = CreateOptions();
        var store = new EnrollmentStateStore(options);
        await store.InitializeAsync(CancellationToken.None);
        var broker = new FakeBroker([account]) { MonitorEnabled = true };
        var reconciler = new EnrollmentReconciler(
            new FakeSafeguard(account),
            broker,
            store,
            options,
            NullLogger<EnrollmentReconciler>.Instance);

        var first = await reconciler.ReconcileAsync(
            ReconcileReason.Manual, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await reconciler.ReconcileAsync(
            ReconcileReason.Manual, DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);

        Assert.True(first.Applied);
        Assert.True(first.MonitorRefreshed);
        Assert.Equal(1, first.EnrolledAccountCount);
        Assert.False(second.Applied);
        Assert.Equal(1, broker.EnsureCalls);
        Assert.Equal(1, broker.SaveCalls);
        Assert.Equal(1, broker.RefreshCalls);
    }

    [Fact]
    public async Task DisabledMonitorIsNotEnabledByEnrollment()
    {
        var account = Account(202, "reporting-user");
        var options = CreateOptions();
        var store = new EnrollmentStateStore(options);
        await store.InitializeAsync(CancellationToken.None);
        var broker = new FakeBroker([account]) { MonitorEnabled = false };
        var reconciler = new EnrollmentReconciler(
            new FakeSafeguard(account),
            broker,
            store,
            options,
            NullLogger<EnrollmentReconciler>.Instance);

        var result = await reconciler.ReconcileAsync(
            ReconcileReason.Manual, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.False(result.MonitorRefreshed);
        Assert.Equal(1, broker.RefreshCalls);
        Assert.False(broker.MonitorEnabled);
    }

    private static IOptions<EnrollmentOptions> CreateOptions() => Options.Create(new EnrollmentOptions
    {
        SafeguardAddress = "spp.example.test",
        SafeguardCertificatePath = "unused.pfx",
        SecretsBrokerBaseUri = "https://broker.example.test",
        AccountGroupId = 42,
        PluginInstanceName = "Bitwarden",
        QuietPeriod = TimeSpan.Zero,
        MaximumBatchAge = TimeSpan.Zero,
        StateDatabasePath = Path.Combine(Path.GetTempPath(), $"wtss-enrollment-{Guid.NewGuid():N}.db")
    });

    private static SafeguardAccount Account(int id, string name) => new()
    {
        Id = id,
        Name = name,
        HasPassword = true,
        Asset = new SafeguardAsset { Id = id + 1000, Name = "sql.example.test" }
    };

    private sealed class FakeSafeguard(params SafeguardAccount[] accounts) : ISafeguardInventoryClient
    {
        public Task<SafeguardSnapshot> GetAccountGroupSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SafeguardSnapshot(accounts, "not-a-real-token"));
    }

    private sealed class FakeBroker(IReadOnlyList<SafeguardAccount> available) : ISecretsBrokerClient
    {
        private readonly IReadOnlyList<SafeguardAccount> _available = available;
        private readonly HashSet<int> _mapped = [];
        public bool MonitorEnabled { get; set; }
        public int EnsureCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task<ISecretsBrokerSession> OpenSessionAsync(
            string accessToken,
            CancellationToken cancellationToken) => Task.FromResult<ISecretsBrokerSession>(new Session(this));

        private sealed class Session(FakeBroker owner) : ISecretsBrokerSession
        {
            public Task<BrokerInventory> GetInventoryAsync(CancellationToken cancellationToken)
            {
                var mappings = owner._mapped.Select(id => new PluginAccountMapping { AccountId = id }).ToArray();
                return Task.FromResult(new BrokerInventory(
                    owner._available,
                    [],
                    mappings,
                    new FullMonitorState { Enabled = owner.MonitorEnabled }));
            }

            public Task<IReadOnlyList<BrokerRetrievableAccount>> EnsureRetrievableAsync(
                IReadOnlyList<SafeguardAccount> accounts,
                CancellationToken cancellationToken)
            {
                owner.EnsureCalls++;
                IReadOnlyList<BrokerRetrievableAccount> result = accounts.Select(account =>
                    new BrokerRetrievableAccount
                    {
                        AccountId = account.Id,
                        AccountName = account.Name,
                        AssetId = account.Asset.Id,
                        AssetName = account.Asset.Name,
                        ApiKey = "redacted-test-value"
                    }).ToArray();
                return Task.FromResult(result);
            }

            public Task SavePluginMappingsAsync(
                IReadOnlyList<BrokerRetrievableAccount> accounts,
                CancellationToken cancellationToken)
            {
                owner.SaveCalls++;
                foreach (var account in accounts)
                {
                    owner._mapped.Add(account.AccountId);
                }

                return Task.CompletedTask;
            }

            public Task<bool> RefreshMonitorAsync(CancellationToken cancellationToken)
            {
                owner.RefreshCalls++;
                return Task.FromResult(owner.MonitorEnabled);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
