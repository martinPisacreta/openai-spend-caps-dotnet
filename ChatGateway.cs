using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LlmIntegration;

public sealed record ChatReply(
    Guid RequestId, int StatusCode, string? Answer, int Attempts, CostRecord Cost);

public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException() : base("Insufficient configured user budget.") { }
}

// The caller supplies an identity established by server-side authentication.
// Neither the provider address nor model is accepted from request JSON.
public sealed class ChatGateway : IDisposable
{
    private readonly ChatPolicy policy;
    private readonly ISpendingLedger ledger;
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string apiKey;

    public ChatGateway(ChatPolicy policy, ISpendingLedger ledger, string apiKey)
        : this(policy, ledger, apiKey,
            new Uri("https://api.openai.com/v1/chat/completions")) { }

    private ChatGateway(
        ChatPolicy policy, ISpendingLedger ledger, string apiKey, Uri endpoint)
    {
        this.policy = policy.Validate();
        this.ledger = ledger;
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Contains('\r') || apiKey.Contains('\n'))
            throw new ArgumentException("A valid server-side API key is required.");
        this.apiKey = apiKey;
        this.endpoint = endpoint;
        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // Only the local test runner can select this endpoint. No real key is used.
    internal static ChatGateway ForTesting(
        ChatPolicy policy, ISpendingLedger ledger, Uri baseAddress)
    {
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != "http" ||
            baseAddress.Host != System.Net.IPAddress.Loopback.ToString() ||
            baseAddress.AbsolutePath != "/" ||
            baseAddress.UserInfo.Length != 0 ||
            baseAddress.Query.Length != 0 || baseAddress.Fragment.Length != 0)
            throw new ArgumentException("Tests require an IPv4 loopback base URL.");
        return new ChatGateway(policy, ledger, "local-test-only",
            new Uri(baseAddress, "v1/chat/completions"));
    }

    public async Task<ChatReply> SendAsync(
        string authenticatedUserId, string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (Encoding.UTF8.GetByteCount(prompt) > policy.MaxPromptUtf8Bytes)
            throw new ArgumentException("Prompt exceeds the configured byte limit.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = policy.Model,
            messages = new[] { new { role = "user", content = prompt } },
            max_completion_tokens = policy.MaxOutputTokens,
            stream = false
        });
        var requestId = Guid.NewGuid();
        if (!ledger.TryReserve(authenticatedUserId, requestId,
                policy.MaximumEstimatedRequestUsd()))
            throw new BudgetExceededException();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(policy.OverallTimeout);
        var token = deadline.Token;
        var attemptExposure = policy.MaximumEstimatedAttemptUsd();
        decimal confirmed = 0, uncertain = 0;
        var attempts = 0;
        var status = 502;
        var outcome = "internal_error";
        string? answer = null;
        CostRecord completed;

        try
        {
            while (attempts < policy.MaxAttempts)
            {
                token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json");

                // Once sending begins, retain exposure until usage is known.
                // Error responses and interrupted calls are conservatively held.
                attempts++;
                uncertain += attemptExposure;
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token);

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    var retryable = code == 429 || code is >= 500 and <= 599;
                    status = retryable ? 503 : 502;
                    outcome = retryable ? "retry_exhausted" : "provider_rejected";
                    if (!retryable || attempts == policy.MaxAttempts) break;

                    var delay = policy.RetryDelay(attempts);
                    var retryAfter = response.Headers.RetryAfter;
                    var requestedDelay = retryAfter?.Delta ??
                        (retryAfter?.Date is DateTimeOffset date
                            ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
                    if (requestedDelay > policy.MaximumRetryDelay)
                    {
                        outcome = "retry_after_exceeds_limit";
                        break;
                    }
                    if (requestedDelay > delay) delay = requestedDelay;
                    await Task.Delay(delay, token);
                    continue;
                }

                var bytes = await ReadBoundedAsync(response.Content, token);
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Invalid provider response.");

                var knownUsage = false;
                if (root.TryGetProperty("usage", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object &&
                    usage.TryGetProperty("prompt_tokens", out var input) &&
                    usage.TryGetProperty("completion_tokens", out var output) &&
                    input.ValueKind == JsonValueKind.Number &&
                    output.ValueKind == JsonValueKind.Number &&
                    input.TryGetInt64(out var inputTokens) && inputTokens >= 0 &&
                    output.TryGetInt64(out var outputTokens) && outputTokens >= 0)
                {
                    confirmed += policy.EstimateUsageUsd(inputTokens, outputTokens);
                    uncertain -= attemptExposure;
                    knownUsage = true;
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0 ||
                    choices[0].ValueKind != JsonValueKind.Object ||
                    !choices[0].TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Missing text response.");

                answer = content.GetString();
                status = 200;
                outcome = knownUsage ? "success" : "success_usage_unknown";
                break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = cancellationToken.IsCancellationRequested ? 499 : 504;
            outcome = cancellationToken.IsCancellationRequested
                ? "client_cancelled" : "overall_timeout";
        }
        catch (HttpRequestException)
        {
            status = 502;
            outcome = "transport_error";
        }
        catch (IOException)
        {
            status = 502;
            outcome = "response_read_error";
        }
        catch (JsonException)
        {
            status = 502;
            outcome = "invalid_provider_json";
        }
        catch (InvalidDataException)
        {
            status = 502;
            outcome = "invalid_provider_response";
        }
        finally
        {
            // Unattempted reservations are released; ambiguous attempts stay held.
            // Record returned usage even if it exceeds the admission estimate.
            completed = ledger.Complete(requestId, confirmed, uncertain, outcome);
        }
        return new ChatReply(requestId, status, answer, attempts, completed);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content, CancellationToken token)
    {
        const int limit = 131072;
        if (content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("Provider response exceeds limit.");
        using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), token);
            if (count == 0) break;
            if (buffer.Length + count > limit)
                throw new InvalidDataException("Provider response exceeds limit.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    public void Dispose() => client.Dispose();
}
