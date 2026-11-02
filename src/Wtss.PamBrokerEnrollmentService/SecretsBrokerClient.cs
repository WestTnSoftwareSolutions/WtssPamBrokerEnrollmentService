using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wtss.PamBrokerEnrollmentService;

public sealed class SecretsBrokerClient(
    IOptions<EnrollmentOptions> options,
    ILogger<SecretsBrokerClient> logger) : ISecretsBrokerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly EnrollmentOptions _options = options.Value;

    public async Task<ISecretsBrokerSession> OpenSessionAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies };
        if (_options.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(EnsureTrailingSlash(_options.SecretsBrokerBaseUri)),
            Timeout = _options.RequestTimeout
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "service/devops/v2/Safeguard/Logon");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "authenticate to Secrets Broker", cancellationToken);
            return new BrokerSession(client, _options.PluginInstanceName, logger);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<T?> GetAsync<T>(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, $"GET {path}", cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private static async Task PutMonitorStateAsync(
        HttpClient client,
        FullMonitorState state,
        CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync(
            "service/devops/v2/Monitor", state, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, $"set monitor enabled={state.Enabled}", cancellationToken);
    }

    private static Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new HttpRequestException(
            $"Secrets Broker failed to {operation}: HTTP {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private sealed class BrokerSession(
        HttpClient client,
        string pluginInstanceName,
        ILogger logger) : ISecretsBrokerSession
    {
        public HttpClient Client { get; } = client;

        public async Task<BrokerInventory> GetInventoryAsync(CancellationToken cancellationToken)
        {
            var available = await GetAsync<SafeguardAccount[]>(Client,
                "service/devops/v2/Safeguard/AvailableAccounts?limit=10000", cancellationToken) ?? [];
            var retrievable = await GetAsync<BrokerRetrievableAccount[]>(Client,
                "service/devops/v2/Safeguard/A2ARegistration/RetrievableAccounts", cancellationToken) ?? [];
            var mappings = await GetAsync<PluginAccountMapping[]>(Client,
                $"service/devops/v2/Plugins/{Uri.EscapeDataString(pluginInstanceName)}/Accounts", cancellationToken) ?? [];
            var monitor = await GetAsync<FullMonitorState>(Client,
                "service/devops/v2/Monitor", cancellationToken) ?? new FullMonitorState();
            return new BrokerInventory(available, retrievable, mappings, monitor);
        }

        public async Task<IReadOnlyList<BrokerRetrievableAccount>> EnsureRetrievableAsync(
            IReadOnlyList<SafeguardAccount> accounts,
            CancellationToken cancellationToken)
        {
            using var response = await Client.PutAsJsonAsync(
                "service/devops/v2/Safeguard/A2ARegistration/RetrievableAccounts",
                accounts,
                JsonOptions,
                cancellationToken);
            await EnsureSuccessAsync(response, "add accounts to the Broker A2A registration", cancellationToken);
            return await response.Content.ReadFromJsonAsync<BrokerRetrievableAccount[]>(JsonOptions, cancellationToken)
                ?? [];
        }

        public async Task SavePluginMappingsAsync(
            IReadOnlyList<BrokerRetrievableAccount> accounts,
            CancellationToken cancellationToken)
        {
            using var response = await Client.PutAsJsonAsync(
                $"service/devops/v2/Plugins/{Uri.EscapeDataString(pluginInstanceName)}/Accounts",
                accounts,
                JsonOptions,
                cancellationToken);
            await EnsureSuccessAsync(response, "save plugin account mappings", cancellationToken);
        }

        public async Task<bool> RefreshMonitorAsync(CancellationToken cancellationToken)
        {
            var state = await GetAsync<FullMonitorState>(Client, "service/devops/v2/Monitor", cancellationToken)
                ?? new FullMonitorState();
            if (!state.Enabled)
            {
                logger.LogWarning("Broker monitor is disabled; mappings were saved but initial credential push is pending.");
                return false;
            }

            var enabledState = new FullMonitorState
            {
                Enabled = true,
                ReverseFlowMonitorState = state.ReverseFlowMonitorState
            };
            var disabledState = new FullMonitorState
            {
                Enabled = false,
                ReverseFlowMonitorState = state.ReverseFlowMonitorState
            };
            await PutMonitorStateAsync(Client, disabledState, cancellationToken);
            await PutMonitorStateAsync(Client, enabledState, cancellationToken);
            logger.LogInformation("Refreshed the Broker monitor; startup initial-password pull has been requested.");
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await Client.GetAsync("service/devops/v2/Safeguard/Logoff", timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Secrets Broker session logoff returned HTTP {StatusCode}.",
                        (int)response.StatusCode);
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Best-effort Secrets Broker session logoff failed.");
            }
            finally
            {
                Client.Dispose();
            }
        }
    }
}
