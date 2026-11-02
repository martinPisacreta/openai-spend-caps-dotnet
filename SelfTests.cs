using System.Collections.Concurrent;
using LlmIntegration;

public static class SelfTests
{
    private const string Success = "{\"choices\":[{\"message\":{\"content\":\"Hello\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}";

    private static ChatPolicy Policy() => new()
    {
        Model = "synthetic-test-model",
        InputUsdPerMillionTokens = 1m,
        OutputUsdPerMillionTokens = 2m,
        MaxPromptUtf8Bytes = 1024,
        InputOverheadTokenAllowance = 16,
        MaxOutputTokens = 16,
        MaxAttempts = 3,
        OverallTimeout = TimeSpan.FromSeconds(3),
        InitialRetryDelay = TimeSpan.FromMilliseconds(20),
        MaximumRetryDelay = TimeSpan.FromMilliseconds(100)
    };

    private static SpendingLedger Ledger(decimal limit = 1m) =>
        new(new Dictionary<string, decimal> { ["alice"] = limit });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        var count = 0;
        async Task Test(string name, Func<Task> action)
        {
            count++;
            try
            {
                await action();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("FAIL " + name + ": " + error);
            }
        }

        await Test("Success records usage and releases unused reservations", async () =>
        {
            await using var fake = await FakeProvider.StartAsync();
            Check(fake.BaseAddress.Host == System.Net.IPAddress.Loopback.ToString(), "Listener must use loopback");
            fake.Enqueue(new(200, Success, TimeSpan.Zero));
            var ledger = Ledger();
            using var gateway = ChatGateway.ForTesting(Policy(), ledger, fake.BaseAddress);
            var reply = await gateway.SendAsync("alice", "Synthetic prompt");
            Check(reply.StatusCode == 200 && reply.Answer == "Hello", "Unexpected reply");
            Check(reply.Attempts == 1 && fake.Attempts == 1, "Unexpected attempts");
            Check(reply.Cost.ConfirmedUsd == 0.000020m, "Incorrect configured-price estimate");
            Check(reply.Cost.UncertainUsd == 0 && ledger.GetBudget("alice").HeldUsd == 0, "Unused exposure retained");
            Check(ledger.GetRecords().Length == 1, "Missing cost record");
        });

        await Test("Concurrent reservations enforce the user cap", () =>
        {
            var ledger = Ledger(0.01m);
            var admitted = new ConcurrentBag<Guid>();
            Parallel.For(0, 100, _ =>
            {
                var id = Guid.NewGuid();
                if (ledger.TryReserve("alice", id, 0.001m)) admitted.Add(id);
            });
            Check(admitted.Count == 10, "Concurrent admission exceeded or underfilled budget");
            Check(ledger.GetBudget("alice").HeldUsd == 0.01m, "Incorrect aggregate reservation");
            foreach (var id in admitted) ledger.Complete(id, 0, 0, "synthetic_unused");
            Check(ledger.GetBudget("alice").AvailableUsd == 0.01m, "Reservations not released");
            return Task.CompletedTask;
        });

        await Test("Budget rejection makes no provider request", async () =>
        {
            await using var fake = await FakeProvider.StartAsync();
            using var gateway = ChatGateway.ForTesting(Policy(), Ledger(0.000001m), fake.BaseAddress);
            var rejected = false;
            try { await gateway.SendAsync("alice", "Synthetic"); }
            catch (BudgetExceededException) { rejected = true; }
            Check(rejected && fake.Attempts == 0, "Rejected request reached provider");
        });

        await Test("429 and 5xx retry with increasing policy delays", async () =>
        {
            var policy = Policy();
            Check(policy.RetryDelay(2) > policy.RetryDelay(1), "Backoff does not increase");
            await using var fake = await FakeProvider.StartAsync();
            fake.Enqueue(new(429, "{}", TimeSpan.Zero));
            fake.Enqueue(new(500, "{}", TimeSpan.Zero));
            fake.Enqueue(new(200, Success, TimeSpan.Zero));
            using var gateway = ChatGateway.ForTesting(policy, Ledger(), fake.BaseAddress);
            var reply = await gateway.SendAsync("alice", "Synthetic");
            Check(reply.StatusCode == 200 && fake.Attempts == 3, "Retry sequence failed");
            Check(reply.Cost.UncertainUsd == 2 * policy.MaximumEstimatedAttemptUsd(), "Ambiguous attempt exposure was released");
        });

        await Test("Retry exhaustion stops at the configured attempt count", async () =>
        {
            var policy = Policy();
            await using var fake = await FakeProvider.StartAsync();
            for (var i = 0; i < policy.MaxAttempts; i++) fake.Enqueue(new(503, "{}", TimeSpan.Zero));
            using var gateway = ChatGateway.ForTesting(policy, Ledger(), fake.BaseAddress);
            var reply = await gateway.SendAsync("alice", "Synthetic");
            Check(reply.StatusCode == 503 && fake.Attempts == policy.MaxAttempts, "Unbounded or missing retries");
            Check(reply.Cost.Outcome == "retry_exhausted", "Incorrect outcome");
            Check(reply.Cost.UncertainUsd == policy.MaximumEstimatedRequestUsd(), "Lost ambiguous exposure");
        });

        await Test("Nonretryable rejection makes one attempt", async () =>
        {
            await using var fake = await FakeProvider.StartAsync();
            fake.Enqueue(new(401, "{}", TimeSpan.Zero));
            using var gateway = ChatGateway.ForTesting(Policy(), Ledger(), fake.BaseAddress);
            var reply = await gateway.SendAsync("alice", "Synthetic");
            Check(reply.StatusCode == 502 && fake.Attempts == 1, "Nonretryable response retried");
            Check(reply.Cost.Outcome == "provider_rejected", "Incorrect rejection record");
        });

        await Test("Overall timeout retains attempted exposure", async () =>
        {
            var policy = Policy() with { OverallTimeout = TimeSpan.FromMilliseconds(150) };
            await using var fake = await FakeProvider.StartAsync();
            fake.Enqueue(new(200, Success, TimeSpan.FromSeconds(1)));
            using var gateway = ChatGateway.ForTesting(policy, Ledger(), fake.BaseAddress);
            var reply = await gateway.SendAsync("alice", "Synthetic");
            Check(reply.StatusCode == 504 && reply.Cost.Outcome == "overall_timeout", "Timeout not enforced");
            Check(reply.Attempts == 1 && reply.Cost.UncertainUsd == policy.MaximumEstimatedAttemptUsd(), "Incorrect timeout accounting");
        });

        await Test("Successful response without usage keeps its reservation", async () =>
        {
            await using var fake = await FakeProvider.StartAsync();
            fake.Enqueue(new(200, "{\"choices\":[{\"message\":{\"content\":\"Hello\"}}]}", TimeSpan.Zero));
            using var gateway = ChatGateway.ForTesting(Policy(), Ledger(), fake.BaseAddress);
            var reply = await gateway.SendAsync("alice", "Synthetic");
            Check(reply.StatusCode == 200 && reply.Cost.Outcome == "success_usage_unknown", "Missing usage incorrectly confirmed");
            Check(reply.Cost.UncertainUsd == Policy().MaximumEstimatedAttemptUsd(), "Unknown cost released");
        });

        await Test("Malformed provider JSON is recorded without retry", async () =>
        {
            await using var fake = await FakeProvider.StartAsync();
            fake.Enqueue(new(200, "{", TimeSpan.Zero));
            using var gateway = ChatGateway.ForTesting(Policy(), Ledger(), fake.BaseAddress);
            var reply = await gateway.SendAsync("alice", "Synthetic");
            Check(reply.StatusCode == 502 && reply.Cost.Outcome == "invalid_provider_json", "Malformed response mishandled");
            Check(fake.Attempts == 1 && reply.Cost.UncertainUsd > 0, "Invalid-response accounting failed");
        });

        Console.WriteLine($"RESULT tests={count} passed={count - failures} failed={failures}");
        return failures == 0 ? 0 : 1;
    }
}
