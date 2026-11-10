@{
    # SPP certificate-user connection. The certificate must be in CurrentUser\My
    # for the Windows identity that runs the Scheduled Task.
    SafeguardAddress               = 'spp.example.com'
    SafeguardApiVersion            = 4
    SafeguardCertificateThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'

    # Secrets Broker address and HTTPS port. Use the configured plugin instance name,
    # not the plugin type or display name.
    SecretsBrokerAddress    = 'broker.example.com'
    SecretsBrokerPort       = 443
    SecretsBrokerApiVersion = 2
    PluginInstanceName      = 'Bitwarden'

    # Account group ID or exact name. Only enabled, password-bearing accounts that
    # Secrets Broker reports as available are eligible.
    AccountGroup = 123

    # Never enable this in production. It disables TLS certificate validation for
    # both SPP and Secrets Broker connections.
    IgnoreSslErrors = $false

    # Contains identifiers and status only; no secrets or access tokens are logged.
    LogPath  = '%ProgramData%\WTSS\PamBrokerEnrollment\enrollment.jsonl'
    MutexName = 'Local\WtssPamBrokerEnrollment'
}
