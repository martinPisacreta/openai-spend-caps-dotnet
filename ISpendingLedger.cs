namespace LlmIntegration;

// Accounting boundary consumed by ChatGateway.
// The authenticated user identity and exposure come from server-side code.
public interface ISpendingLedger
{
    // Return false only for a known admission rejection. Storage failures and
    // ambiguous outcomes must throw so the caller does not dispatch a request.
    // An existing request ID must never authorize a second provider dispatch.
    bool TryReserve(string userId, Guid requestId, decimal exposureUsd);

    // Release only proven-unused exposure; uncertain provider charges stay held.
    // A persistent implementation must preserve accounting operation identities
    // across retries and must not silently discard an observed cost overrun.
    CostRecord Complete(
        Guid requestId, decimal confirmedUsd, decimal uncertainUsd, string outcome);
}
