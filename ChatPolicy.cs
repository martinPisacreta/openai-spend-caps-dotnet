namespace LlmIntegration;

// Server-side configuration only. Never populate this policy from request JSON.
// Prices must be supplied explicitly for the selected model; no current market
// price is assumed here. Returned usage produces an estimate, not an invoice.
public sealed record ChatPolicy
{
    public required string Model { get; init; }
    public required decimal InputUsdPerMillionTokens { get; init; }
    public required decimal OutputUsdPerMillionTokens { get; init; }
    public int MaxPromptUtf8Bytes { get; init; } = 8192;
    public int InputOverheadTokenAllowance { get; init; } = 1024;
    public int MaxOutputTokens { get; init; } = 128;
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    public ChatPolicy Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200)
            throw new ArgumentException("A server-configured model is required.");
        if (InputUsdPerMillionTokens <= 0 || InputUsdPerMillionTokens > 1000000m ||
            OutputUsdPerMillionTokens <= 0 || OutputUsdPerMillionTokens > 1000000m)
            throw new ArgumentException("Explicit positive model prices are required.");
        if (MaxPromptUtf8Bytes is < 1 or > 32768 ||
            InputOverheadTokenAllowance is < 1 or > 32768 ||
            MaxOutputTokens is < 1 or > 4096 ||
            MaxAttempts is < 1 or > 5)
            throw new ArgumentException("Request limits are outside supported bounds.");
        if (OverallTimeout < TimeSpan.FromMilliseconds(100) ||
            OverallTimeout > TimeSpan.FromMinutes(2) ||
            InitialRetryDelay <= TimeSpan.Zero ||
            MaximumRetryDelay < InitialRetryDelay ||
            MaximumRetryDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Invalid timeout or retry delays.");
        return this;
    }

    // Admission estimate for a single text-only user message: allow one input
    // token per UTF-8 byte plus configured message overhead. This is not a
    // tokenizer or a provider billing guarantee. No tools or images are included.
    public decimal MaximumEstimatedAttemptUsd()
    {
        Validate();
        return checked(
            ((MaxPromptUtf8Bytes + InputOverheadTokenAllowance) *
                InputUsdPerMillionTokens +
             MaxOutputTokens * OutputUsdPerMillionTokens) / 1000000m);
    }

    // Reserve every permitted attempt, including potentially charged retries.
    public decimal MaximumEstimatedRequestUsd() =>
        checked(MaximumEstimatedAttemptUsd() * MaxAttempts);

    public decimal EstimateUsageUsd(long promptTokens, long completionTokens)
    {
        Validate();
        if (promptTokens < 0 || completionTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(promptTokens),
                "Usage counts must be nonnegative.");
        return checked(
            (promptTokens * InputUsdPerMillionTokens +
             completionTokens * OutputUsdPerMillionTokens) / 1000000m);
    }

    public TimeSpan RetryDelay(int completedAttempts)
    {
        Validate();
        if (completedAttempts < 1 || completedAttempts >= MaxAttempts)
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        var milliseconds = Math.Min(
            MaximumRetryDelay.TotalMilliseconds,
            InitialRetryDelay.TotalMilliseconds *
                Math.Pow(2, completedAttempts - 1));
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
