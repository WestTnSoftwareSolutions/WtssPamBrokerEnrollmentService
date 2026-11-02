using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Wtss.PamBrokerEnrollmentService;

public enum ReconcileReason { Startup, Poll, Manual, Settlement }

public sealed record ReconcileResult(
    int GroupAccountCount,
    int EligibleAccountCount,
    int PendingAccountCount,
    int EnrolledAccountCount,
    bool Applied,
    bool MonitorRefreshed,
    IReadOnlyList<int> AccountsNoLongerSelected);

public sealed class BatchWindow
{
    private HashSet<int> _pendingIds = [];
    private DateTimeOffset? _firstSeen;
    private DateTimeOffset? _lastChanged;

    public bool Observe(IEnumerable<int> ids, DateTimeOffset now)
    {
        var incoming = ids.ToHashSet();
        if (incoming.Count == 0)
        {
            Clear();
            return false;
        }

        if (_pendingIds.SetEquals(incoming))
        {
            return false;
        }

        _pendingIds = incoming;
        _firstSeen ??= now;
        _lastChanged = now;
        return true;
    }

    public bool IsDue(DateTimeOffset now, TimeSpan quietPeriod, TimeSpan maximumAge) =>
        _pendingIds.Count > 0 &&
        ((_lastChanged.HasValue && now - _lastChanged.Value >= quietPeriod) ||
         (_firstSeen.HasValue && now - _firstSeen.Value >= maximumAge));

    public void Clear()
    {
        _pendingIds = [];
        _firstSeen = null;
        _lastChanged = null;
    }
}

public sealed class EnrollmentReconciler(
    ISafeguardInventoryClient safeguard,
    ISecretsBrokerClient broker,
    EnrollmentStateStore stateStore,
    IOptions<EnrollmentOptions> options,
    ILogger<EnrollmentReconciler> logger)
{
    private readonly EnrollmentOptions _options = options.Value;
    private readonly BatchWindow _batch = new();

    public async Task<ReconcileResult> ReconcileAsync(
        ReconcileReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var snapshot = await safeguard.GetAccountGroupSnapshotAsync(cancellationToken);
        try
        {
            await using var brokerSession = await broker.OpenSessionAsync(snapshot.AccessToken, cancellationToken);
            var inventory = await brokerSession.GetInventoryAsync(cancellationToken);
            var availableIds = inventory.AvailableAccounts.Select(static account => account.Id).ToHashSet();
            var eligible = snapshot.Accounts
                .Where(account => !account.Disabled && account.HasPassword && availableIds.Contains(account.Id))
                .ToArray();
            await stateStore.ObserveAsync(eligible, _options.PluginInstanceName, now, cancellationToken);

            var mappedIds = inventory.PluginMappings.Select(static mapping => mapping.AccountId).ToHashSet();
            var additions = eligible.Where(account => !mappedIds.Contains(account.Id)).ToArray();
            var groupIds = snapshot.Accounts.Select(static account => account.Id).ToHashSet();
            var ownedIds = await stateStore.GetOwnedAccountIdsAsync(_options.PluginInstanceName, cancellationToken);
            var noLongerSelected = ownedIds.Where(id => !groupIds.Contains(id)).Order().ToArray();
            if (noLongerSelected.Length > 0)
            {
                logger.LogWarning(
                    "{Count} service-owned accounts are no longer in the selected group. Add-only mode leaves them mapped: {Ids}.",
                    noLongerSelected.Length,
                    string.Join(',', noLongerSelected));
            }

            _batch.Observe(additions.Select(static account => account.Id), now);
            var force = reason is ReconcileReason.Manual or ReconcileReason.Settlement;
            var due = force || _batch.IsDue(now, _options.QuietPeriod, _options.MaximumBatchAge);
            if (additions.Length == 0)
            {
                var refreshed = reason == ReconcileReason.Settlement &&
                    await brokerSession.RefreshMonitorAsync(cancellationToken);
                return new ReconcileResult(snapshot.Accounts.Count, eligible.Length, 0, 0, false, refreshed,
                    noLongerSelected);
            }

            if (!due)
            {
                logger.LogInformation("Batching {Count} new accounts until the quiet or maximum-age window is reached.",
                    additions.Length);
                return new ReconcileResult(snapshot.Accounts.Count, eligible.Length, additions.Length, 0, false, false,
                    noLongerSelected);
            }

            var retrievable = await brokerSession.EnsureRetrievableAsync(additions, cancellationToken);
            var desiredIds = additions.Select(static account => account.Id).ToHashSet();
            var mappings = retrievable.Where(account => desiredIds.Contains(account.AccountId)).ToArray();
            var missingIds = desiredIds.Except(mappings.Select(static mapping => mapping.AccountId)).ToArray();
            if (missingIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Broker did not return A2A entries for account IDs: {string.Join(',', missingIds)}.");
            }

            await brokerSession.SavePluginMappingsAsync(mappings, cancellationToken);
            await stateStore.MarkEnrolledAsync(additions, _options.PluginInstanceName, now, cancellationToken);
            var monitorRefreshed = await brokerSession.RefreshMonitorAsync(cancellationToken);
            _batch.Clear();
            logger.LogInformation("Enrolled {Count} accounts into plugin {PluginName}.",
                additions.Length, _options.PluginInstanceName);
            return new ReconcileResult(snapshot.Accounts.Count, eligible.Length, 0, additions.Length, true,
                monitorRefreshed, noLongerSelected);
        }
        finally
        {
            // Strings are immutable; keep the high-value token lifetime bounded to this pass and never log it.
            snapshot = snapshot with { AccessToken = string.Empty };
        }
    }
}

public sealed class ReconcileSignal
{
    private readonly Channel<ReconcileReason> _channel = Channel.CreateBounded<ReconcileReason>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Request(ReconcileReason reason) => _channel.Writer.TryWrite(reason);
    public ValueTask<ReconcileReason> ReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}

public sealed class RuntimeStatus
{
    private readonly object _gate = new();
    public bool Initialized { get; private set; }
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string? LastError { get; private set; }
    public ReconcileResult? LastResult { get; private set; }

    public void Success(DateTimeOffset now, ReconcileResult result)
    {
        lock (_gate)
        {
            Initialized = true;
            LastAttemptUtc = now;
            LastSuccessUtc = now;
            LastError = null;
            LastResult = result;
        }
    }

    public void Failure(DateTimeOffset now, Exception exception)
    {
        lock (_gate)
        {
            Initialized = true;
            LastAttemptUtc = now;
            LastError = exception.GetType().Name;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new { Initialized, LastAttemptUtc, LastSuccessUtc, LastError, LastResult };
        }
    }
}

public sealed class EnrollmentWorker(
    EnrollmentReconciler reconciler,
    EnrollmentStateStore stateStore,
    ReconcileSignal signal,
    RuntimeStatus status,
    IOptions<EnrollmentOptions> options,
    ILogger<EnrollmentWorker> logger) : BackgroundService
{
    private readonly EnrollmentOptions _options = options.Value;
    private int _settlementScheduled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await stateStore.InitializeAsync(stoppingToken);
        signal.Request(ReconcileReason.Startup);
        var pollProducer = ProducePollSignalsAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var reason = await signal.ReadAsync(stoppingToken);
                await RunPassAsync(reason, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }

        try
        {
            await pollProducer;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
    }

    private async Task ProducePollSignalsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            signal.Request(ReconcileReason.Poll);
        }
    }

    private async Task RunPassAsync(ReconcileReason reason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var result = await reconciler.ReconcileAsync(reason, now, cancellationToken);
            status.Success(now, result);
            if (result.Applied && Interlocked.Exchange(ref _settlementScheduled, 1) == 0)
            {
                _ = ScheduleSettlementAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            status.Failure(now, exception);
            logger.LogError(exception, "Enrollment reconciliation failed.");
        }
    }

    private async Task ScheduleSettlementAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.SettlementDelay, cancellationToken);
            signal.Request(ReconcileReason.Settlement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        finally
        {
            Interlocked.Exchange(ref _settlementScheduled, 0);
        }
    }
}
