using System.ComponentModel.DataAnnotations;

namespace Wtss.PamBrokerEnrollmentService;

public sealed class EnrollmentOptions
{
    public const string SectionName = "Enrollment";

    [Required] public string SafeguardAddress { get; init; } = string.Empty;
    [Range(1, 99)] public int SafeguardApiVersion { get; init; } = 4;
    [Required] public string SafeguardCertificatePath { get; init; } = string.Empty;
    public string SafeguardCertificatePassphraseEnvironmentVariable { get; init; } =
        "WTSS_SPP_CERTIFICATE_PASSPHRASE";
    [Required, Url] public string SecretsBrokerBaseUri { get; init; } = string.Empty;
    [Range(1, int.MaxValue)] public int AccountGroupId { get; init; }
    [Required] public string PluginInstanceName { get; init; } = string.Empty;
    public bool IgnoreSslErrors { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumBatchAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan SettlementDelay { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    [Required] public string StateDatabasePath { get; init; } = "data/enrollment-state.db";
}

