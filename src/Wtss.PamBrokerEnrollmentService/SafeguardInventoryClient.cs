using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OneIdentity.SafeguardDotNet;

namespace Wtss.PamBrokerEnrollmentService;

public sealed class SafeguardInventoryClient(
    IOptions<EnrollmentOptions> options,
    ILogger<SafeguardInventoryClient> logger) : ISafeguardInventoryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly EnrollmentOptions _options = options.Value;

    public Task<SafeguardSnapshot> GetAccountGroupSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var certificate = File.ReadAllBytes(Path.GetFullPath(_options.SafeguardCertificatePath));
        var passphraseValue = string.IsNullOrWhiteSpace(_options.SafeguardCertificatePassphraseEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(_options.SafeguardCertificatePassphraseEnvironmentVariable);
        using var passphrase = ToSecureString(passphraseValue);
        using var connection = Safeguard.Connect(
            _options.SafeguardAddress,
            certificate,
            passphrase,
            _options.SafeguardApiVersion,
            _options.IgnoreSslErrors);

        var response = connection.InvokeMethodFull(
            Service.Core,
            Method.Get,
            $"AccountGroups/{_options.AccountGroupId}");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Safeguard returned HTTP {(int)response.StatusCode} while reading account group {_options.AccountGroupId}.");
        }

        var group = JsonSerializer.Deserialize<SafeguardAccountGroup>(response.Body, JsonOptions)
            ?? throw new InvalidOperationException("Safeguard returned an empty account-group response.");
        var token = ToPlainText(connection.GetAccessToken());
        logger.LogInformation("Read {Count} accounts from Safeguard account group {GroupId} ({GroupName}).",
            group.Accounts.Length, group.Id, group.Name);
        return Task.FromResult(new SafeguardSnapshot(group.Accounts, token));
    }

    private static SecureString? ToSecureString(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var result = new SecureString();
        foreach (var character in value)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    private static string ToPlainText(SecureString value)
    {
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(value);
            return Marshal.PtrToStringUni(pointer)
                ?? throw new InvalidOperationException("Safeguard returned an empty access token.");
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
        }
    }
}
