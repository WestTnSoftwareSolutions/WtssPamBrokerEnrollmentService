using Wtss.PamBrokerEnrollmentService;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "WTSS PAM Broker Enrollment Service");

builder.Services
    .AddOptions<EnrollmentOptions>()
    .Bind(builder.Configuration.GetSection(EnrollmentOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(static options => options.PollInterval > TimeSpan.Zero, "PollInterval must be positive.")
    .Validate(static options => options.MaximumBatchAge >= options.QuietPeriod,
        "MaximumBatchAge must be greater than or equal to QuietPeriod.")
    .ValidateOnStart();

builder.Services.AddSingleton<RuntimeStatus>();
builder.Services.AddSingleton<ReconcileSignal>();
builder.Services.AddSingleton<EnrollmentStateStore>();
builder.Services.AddSingleton<ISafeguardInventoryClient, SafeguardInventoryClient>();
builder.Services.AddSingleton<ISecretsBrokerClient, SecretsBrokerClient>();
builder.Services.AddSingleton<EnrollmentReconciler>();
builder.Services.AddHostedService<EnrollmentWorker>();

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (RuntimeStatus status) =>
    status.Initialized && string.IsNullOrWhiteSpace(status.LastError)
        ? Results.Ok(status.Snapshot())
        : Results.Json(status.Snapshot(), statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/api/status", (RuntimeStatus status) => Results.Ok(status.Snapshot()));
app.MapPost("/api/reconcile", (ReconcileSignal signal) =>
{
    signal.Request(ReconcileReason.Manual);
    return Results.Accepted(value: new { status = "queued" });
});

await app.RunAsync();

