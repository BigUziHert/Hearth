using System.Net;
using System.Threading.RateLimiting;
using Hearth.Core;
using Hearth.Host;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

using var launcherLifetime = await LauncherLifetime.ConnectAsync(args);
if (!launcherLifetime.MayStart) return;
var arguments = HostArguments.Parse(args);
if (arguments.Initialize)
{
    HostStore.Initialize(arguments);
    Console.WriteLine("Hearth host initialized. Owner credentials are stored only as a hash.");
    return;
}
using var store = new HostStore(arguments);
using var hostCertificate = arguments.HttpsPort is null ? null : HostCertificate.LoadOrCreate(arguments.Data);
if (arguments.HttpsPort == (arguments.Port ?? store.Settings.Port)) throw new ArgumentException("HTTP and HTTPS need different ports.");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = "HearthHost");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    foreach (var converter in JsonDefaults.Options.Converters) options.SerializerOptions.Converters.Add(converter);
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = HostStore.MaxBlobBytes;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    options.Listen(IPAddress.Parse(arguments.Bind ?? store.Settings.Bind), arguments.Port ?? store.Settings.Port);
    if (arguments.HttpsPort is int httpsPort)
        options.Listen(IPAddress.Any, httpsPort, listener => listener.UseHttps(hostCertificate!));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("join", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (!context.Request.Path.Value!.Contains("/blobs/", StringComparison.Ordinal))
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 2 * 1024 * 1024;
    }
    try { await next(context); }
    catch (HostException exception)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = exception.Status;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    catch (BadHttpRequestException exception)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid or oversized request." });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "The host could not complete the request. Retry, or ask the owner to check host logs." });
    }
});
app.UseRateLimiter();
app.MapGet("/health", () => new HealthResponse(store.Settings.Name, store.Settings.HostId, typeof(HostStore).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", DateTimeOffset.UtcNow));
app.MapGet("/owner/packs", (HttpContext context) => store.ListOwned(Bearer(context)));
app.MapGet("/owner/status", (HttpContext context) => store.Status(Bearer(context)));
app.MapGet("/owner/endpoints", (HttpContext context) => store.Endpoints(Bearer(context)));
app.MapPost("/owner/packs", (HttpContext context, CreatePackRequest request) => store.CreatePack(Bearer(context), request));
app.MapGet("/packs/{id}/access", (HttpContext context, string id) => store.Access(id, Bearer(context), InternetPlayerRequest(context)));
app.MapPost("/packs/{id}/join", (string id, JoinRequest request) => store.Join(id, request)).RequireRateLimiting("join");
app.MapGet("/packs/{id}/members", (HttpContext context, string id) => store.Members(id, Bearer(context)));
app.MapPatch("/packs/{id}/members/{memberId}", (HttpContext context, string id, string memberId, MemberUpdate request) => store.UpdateMember(id, memberId, Bearer(context), request));
app.MapDelete("/packs/{id}/invite", (HttpContext context, string id) => store.RotateInvite(id, Bearer(context)));
app.MapPatch("/packs/{id}/invite", (HttpContext context, string id, SharedInviteUpdate request) => store.SetSharedInvite(id, Bearer(context), request));
app.MapGet("/packs/{id}/invites", (HttpContext context, string id) => store.PersonalInvites(id, Bearer(context)));
app.MapPost("/packs/{id}/invites", (HttpContext context, string id, CreatePersonalInviteRequest request) => store.CreatePersonalInvite(id, Bearer(context), request));
app.MapDelete("/packs/{id}/invites/{inviteId}", (HttpContext context, string id, string inviteId) => store.RevokePersonalInvite(id, inviteId, Bearer(context)));
app.MapPost("/packs/{id}/members/decline-all", (HttpContext context, string id) => store.DeclineAll(id, Bearer(context)));
app.MapPost("/packs/{id}/archive", (HttpContext context, string id) => store.ArchivePack(id, Bearer(context)));
app.MapGet("/packs/{id}/releases/{releaseId}/manifest", (HttpContext context, string id, string releaseId) => store.ReleaseManifest(id, releaseId, Bearer(context)));
app.MapGet("/packs/{id}/manifest", (HttpContext context, string id) =>
{
    var manifest = store.Manifest(id, Bearer(context));
    store.NotePlayerAccess(id, Bearer(context), "manifest", InternetPlayerRequest(context));
    return manifest;
});
app.MapPut("/packs/{id}/blobs/{sha256}", async (HttpContext context, string id, string sha256) =>
{
    await store.UploadBlob(id, sha256, Bearer(context), context.Request.Body, context.RequestAborted);
    return Results.Ok(new { sha256 = sha256.ToLowerInvariant() });
});
app.MapGet("/packs/{id}/blobs/{sha256}", (HttpContext context, string id, string sha256) =>
{
    var path = store.DownloadBlob(id, sha256, Bearer(context));
    store.NotePlayerAccess(id, Bearer(context), "download", InternetPlayerRequest(context));
    return Results.File(path, "application/java-archive", enableRangeProcessing: true);
});
app.MapPost("/packs/{id}/releases", (HttpContext context, string id, PublishRequest request) => store.Publish(id, Bearer(context), request));
if (launcherLifetime.IsManaged)
{
    await app.StartAsync(launcherLifetime.Stopping);
    Console.WriteLine(LauncherLifetime.ReadyMessage);
    await app.WaitForShutdownAsync(launcherLifetime.Stopping);
}
else await app.RunAsync();

// Only the authenticated direct TLS peer is evidence. Forwarded headers are untrusted and ignored.
static bool InternetPlayerRequest(HttpContext context) => HostNetworkAddress.ShouldConfirmInternet(context.Request.IsHttps, context.Connection.RemoteIpAddress);

static string Bearer(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length > 1024)
        throw new HostException(401, "A Hearth access credential is required.");
    return header[7..].Trim();
}
