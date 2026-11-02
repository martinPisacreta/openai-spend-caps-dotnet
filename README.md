# OpenAI spending caps in .NET

A .NET 8 sample that authenticates users, reserves an estimated request cost against each user's configured budget, and sends text requests to the OpenAI Chat Completions endpoint. It includes a local scripted provider and a self-test runner.

## Run with the fake provider, without an API key

Prerequisite: the .NET 8 SDK. From this directory, run:

```sh
dotnet run --project LlmIntegration.csproj -- --self-test
```

The self-test branch runs before environment configuration is loaded. It requires no API key or configured users. It starts local loopback listeners, supplies synthetic responses, and does not call OpenAI. The default project has no external package references enabled; the SDK may perform its normal restore step.

The defined cases cover successful usage accounting, concurrent reservations, budget rejection before a provider request, retries, retry exhaustion, nonretryable errors, overall timeout, missing usage, and malformed JSON. The runner prints PASS or FAIL and returns a nonzero exit code on failure. See Verification status below for the latest recorded run.

## How the per-user budget works

The HTTP endpoint derives the user identity from a server-configured bearer token. Request JSON contains only a prompt; it cannot select the user, model, prices, provider endpoint, or budget.

Before sending, the gateway reserves the estimated maximum exposure for every permitted attempt. The in-memory ledger coordinates concurrent reservations. When the available budget cannot cover a reservation, the request is rejected without calling the provider.

Reported token usage is converted to a cost estimate using the configured prices. Unattempted exposure is released. Attempted requests with unknown usage, including ambiguous failures, retain estimated exposure. Retry attempts are included in accounting.

This is an application admission control based on estimates, not a guarantee about the provider's final bill. The input allowance is not a tokenizer. Returned usage can exceed the admission estimate and is still recorded. Model prices must be maintained by the operator.

The default memory ledger lasts only for the process lifetime and does not coordinate separate instances. Restarting resets its records and configured budgets. There is no automatic daily or monthly budget reset.

## Configure the real provider

Normal execution uses the fixed OpenAI endpoint in ChatGateway.cs. Configure the following environment variables on the server:

| Variable | Purpose |
| --- | --- |
| OPENAI_API_KEY | Real provider credential, supplied through the environment or a secret manager |
| LLM_POLICY_JSON | ChatPolicy JSON with Model, InputUsdPerMillionTokens, and OutputUsdPerMillionTokens; optional request and retry limits |
| LLM_USERS_JSON | Object mapping each user ID to an ApiToken and positive BudgetUsd |
| LLM_LEDGER_MODE | Use memory for this standalone sample; memory is the default |
| LLM_PORT | Optional local listener port; defaults to 5080 |

Each user token must be distinct, contain 32–256 printable ASCII characters, and should be generated securely. Keep provider credentials and user tokens out of source control, request bodies, and command-line arguments.

For example, LLM_USERS_JSON has this shape; replace the placeholder token with a securely generated token before starting:

```json
{
  "alice": {
    "ApiToken": "REPLACE_WITH_A_SECURE_RANDOM_TOKEN",
    "BudgetUsd": 1.00
  }
}
```

Set Model to a model available to your account and both price fields to its verified positive USD-per-million-token rates. No current model or tariff is assumed by this sample. Optional policy fields and defaults are defined in ChatPolicy.cs.

After configuring the environment, start normal execution:

```sh
dotnet run --project LlmIntegration.csproj
```

Send POST /chat to the local listener with Content-Type: application/json, an Authorization bearer header containing the configured user token, and this body:

```json
{ "prompt": "Hello" }
```

Normal execution can incur real provider charges. It does not use FakeProvider. Application cost logs contain accounting metadata rather than prompt or answer text.

## Optional SQL adapter

SpendingLedger.cs supplies the default in-memory implementation. SqlSpendingLedger.cs and LedgerConfiguration.cs retain an optional SQL Server adapter. SQL operation requires its own provisioned schema, balances, permissions, connection configuration, and validation; this standalone package does not supply database provisioning. The conditional EnableSqlServer project option adds Microsoft.Data.SqlClient from the configured package sources. Use memory mode for the no-key self-tests.

## Verification status

Checked on Windows on 2 November 2026 with the .NET SDK 10.0.402 targeting net8.0:

- `dotnet build -c Release`: succeeded with 0 warnings.
- `dotnet run -- --self-test`: 9 of 9 cases passed against the local fake provider.
- HTTP smoke test on 127.0.0.1 with a placeholder provider key: requests without a token or with a wrong token returned 401, malformed JSON and an extra `user` field returned 400, and a request above the configured budget returned 429 before any provider call.

It has not been validated against the live OpenAI API. Local fake-provider tests cannot establish live model compatibility, current pricing, or actual provider billing.

## License

MIT, copyright 2026 Martin Pisacreta. See LICENSE.
