namespace LlmIntegration;

public sealed record CostRecord(
    Guid RequestId, string UserId, decimal ReservedUsd,
    decimal ConfirmedUsd, decimal UncertainUsd, string Outcome,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    bool ReservationExceeded);

public sealed record BudgetSnapshot(
    decimal LimitUsd, decimal ConfirmedUsd, decimal HeldUsd,
    decimal AvailableUsd);

// Process-local accounting. Restarting loses this ledger. A production deployment
// requires durable atomic reservations shared by every application instance.
// Confirmed means calculated from returned usage and configured prices; it does
// not mean reconciled against the provider's invoice. Uncertain charges stay held.
public sealed class SpendingLedger : ISpendingLedger
{
    private readonly object gate = new();
    private readonly Dictionary<string, Account> accounts;
    private readonly Dictionary<Guid, CostRecord> records = new();

    private sealed class Account(decimal limit)
    {
        public decimal Limit { get; } = limit;
        public decimal Confirmed { get; set; }
        public decimal Held { get; set; }
    }

    public SpendingLedger(IReadOnlyDictionary<string, decimal> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        accounts = new(StringComparer.Ordinal);
        foreach (var pair in limits)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0)
                throw new ArgumentException(
                    "Each configured user needs a positive USD limit.",
                    nameof(limits));
            accounts.Add(pair.Key, new Account(pair.Value));
        }
    }

    // userId must come from server-side authentication, never the request body.
    // Reserve the maximum estimated exposure for ALL permitted HTTP attempts
    // before making the first provider call. Unknown outcomes must not be freed.
    public bool TryReserve(string userId, Guid requestId, decimal exposureUsd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (requestId == Guid.Empty)
            throw new ArgumentException("A request ID is required.", nameof(requestId));
        if (exposureUsd <= 0)
            throw new ArgumentOutOfRangeException(nameof(exposureUsd));

        lock (gate)
        {
            if (records.ContainsKey(requestId))
                throw new InvalidOperationException("Request ID already recorded.");
            if (!accounts.TryGetValue(userId, out var account))
                return false;
            var available = account.Limit - account.Confirmed - account.Held;
            if (exposureUsd > available)
                return false;

            var nextHeld = checked(account.Held + exposureUsd);
            records.Add(requestId, new CostRecord(
                requestId, userId, exposureUsd, 0, 0, "reserved",
                DateTimeOffset.UtcNow, null, false));
            account.Held = nextHeld;
            return true;
        }
    }

    // Release only exposure proven unused. For timeout, transport failure or
    // missing usage, retain the relevant attempt's maximum estimated charge.
    // Report an overrun honestly: recording actual usage must never fail merely
    // because pricing or token estimates were too low. Future admission blocks
    // automatically when confirmed costs plus holds exhaust the account limit.
    public CostRecord Complete(
        Guid requestId, decimal confirmedUsd, decimal uncertainUsd, string outcome)
    {
        if (confirmedUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(confirmedUsd));
        if (uncertainUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(uncertainUsd));
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        lock (gate)
        {
            if (!records.TryGetValue(requestId, out var original))
                throw new KeyNotFoundException("Unknown request ID.");
            if (original.CompletedAt is not null)
                throw new InvalidOperationException("Request already completed.");

            var account = accounts[original.UserId];
            var totalExposure = checked(confirmedUsd + uncertainUsd);
            var nextConfirmed = checked(account.Confirmed + confirmedUsd);
            var nextHeld = checked(
                account.Held - original.ReservedUsd + uncertainUsd);
            var completed = original with
            {
                ConfirmedUsd = confirmedUsd,
                UncertainUsd = uncertainUsd,
                Outcome = outcome,
                CompletedAt = DateTimeOffset.UtcNow,
                ReservationExceeded = totalExposure > original.ReservedUsd
            };
            account.Confirmed = nextConfirmed;
            account.Held = nextHeld;
            records[requestId] = completed;
            return completed;
        }
    }

    public BudgetSnapshot GetBudget(string userId)
    {
        lock (gate)
        {
            if (!accounts.TryGetValue(userId, out var account))
                throw new KeyNotFoundException("Unknown configured user.");
            return new BudgetSnapshot(
                account.Limit, account.Confirmed, account.Held,
                Math.Max(0m, account.Limit - account.Confirmed - account.Held));
        }
    }

    public CostRecord[] GetRecords()
    {
        lock (gate)
            return records.Values.ToArray();
    }
}
