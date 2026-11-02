using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmIntegration;

if (args.Length == 1 && args[0] == "--self-test")
{
    Environment.ExitCode = await SelfTests.RunAsync();
    return;
}

// All identities, budgets, prices and credentials come from server configuration.
// Never put real secrets in source control or command-line arguments.
static string RequiredEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing configuration: {name}");

var configurationJson = new JsonSerializerOptions
{
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
};
var policy = JsonSerializer.Deserialize<ChatPolicy>(
    RequiredEnvironment("LLM_POLICY_JSON"), configurationJson)
    ?.Validate() ?? throw new InvalidOperationException("Invalid chat policy.");
var users = JsonSerializer.Deserialize<Dictionary<string, ConfiguredUser>>(
    RequiredEnvironment("LLM_USERS_JSON"), configurationJson)
    ?? throw new InvalidOperationException("Invalid user configuration.");
if (users.Count is < 1 or > 1000)
    throw new InvalidOperationException("Configure between 1 and 1000 users.");

var tokenHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
var uniqueTokens = new HashSet<string>(StringComparer.Ordinal);
foreach (var pair in users)
{
    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 100 ||
        pair.Key.Any(char.IsControl) || pair.Value is null ||
        pair.Value.BudgetUsd <= 0 ||
        string.IsNullOrWhiteSpace(pair.Value.ApiToken) ||
        pair.Value.ApiToken.Length is < 32 or > 256 ||
        pair.Value.ApiToken.Any(character => character < '!' || character > '~'))
        throw new InvalidOperationException("Invalid user or token configuration.");
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value.ApiToken));
    if (!uniqueTokens.Add(Convert.ToHexString(hash)))
        throw new InvalidOperationException("Every user needs a distinct token.");
    tokenHashes.Add(pair.Key, hash);
}

var portText = Environment.GetEnvironmentVariable("LLM_PORT") ?? "5080";
if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535)
    throw new InvalidOperationException("Invalid LLM_PORT.");

var ledger = LedgerConfiguration.Create(users.ToDictionary(
    pair => pair.Key, pair => pair.Value.BudgetUsd, StringComparer.Ordinal));
using var gateway = new ChatGateway(
    policy, ledger, RequiredEnvironment("OPENAI_API_KEY"));

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    options.Limits.MaxRequestBodySize = 65536;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling =
        JsonUnmappedMemberHandling.Disallow;
});
var app = builder.Build();

app.MapPost("/chat", async Task<IResult> (HttpContext context) =>
{
    var authorization = context.Request.Headers.Authorization;
    if (authorization.Count != 1)
        return Results.StatusCode(401);
    var header = authorization[0];
    if (header is null || header.Length > 300 ||
        !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(401);

    var suppliedToken = header[7..];
    if (suppliedToken.Length is < 32 or > 256)
        return Results.StatusCode(401);
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
    string? userId = null;
    foreach (var pair in tokenHashes)
        if (CryptographicOperations.FixedTimeEquals(suppliedHash, pair.Value))
            userId = pair.Key;
    if (userId is null)
        return Results.StatusCode(401);

    if (!context.Request.HasJsonContentType())
        return Results.StatusCode(415);

    ChatRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<ChatRequest>(
            cancellationToken: context.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid request JSON." });
    }
    catch (BadHttpRequestException exception)
    {
        return Results.StatusCode(exception.StatusCode == 413 ? 413 : 400);
    }
    catch (OperationCanceledException)
        when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }

    if (request is null || string.IsNullOrWhiteSpace(request.Prompt) ||
        Encoding.UTF8.GetByteCount(request.Prompt) > policy.MaxPromptUtf8Bytes)
        return Results.BadRequest(new { error = "Prompt is missing or too large." });

    try
    {
        var reply = await gateway.SendAsync(
            userId, request.Prompt, context.RequestAborted);
        // Only accounting metadata is logged; no prompt, answer, key or headers.
        app.Logger.LogInformation("Chat cost record: {CostRecord}",
            JsonSerializer.Serialize(reply.Cost));
        return Results.Json(reply, statusCode: reply.StatusCode);
    }
    catch (BudgetExceededException)
    {
        return Results.Json(
            new { error = "Insufficient configured budget." }, statusCode: 429);
    }
});

await app.RunAsync();

public sealed record ConfiguredUser(string ApiToken, decimal BudgetUsd);
public sealed record ChatRequest(string Prompt);
