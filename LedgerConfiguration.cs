using System.Data.Common;
using System.Globalization;
using System.Reflection;

namespace LlmIntegration;

// Program.cs must explicitly call Create to enable this selector.
// SQL mode requires Microsoft.Data.SqlClient in the consuming project.
public static class LedgerConfiguration
{
    public static ISpendingLedger Create(
        IReadOnlyDictionary<string, decimal> configuredBudgets)
    {
        ArgumentNullException.ThrowIfNull(configuredBudgets);
        var mode = Environment.GetEnvironmentVariable(
            "LLM_LEDGER_MODE") ?? "memory";

        if (mode == "memory")
            return new SpendingLedger(configuredBudgets.ToDictionary(
                pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal));

        if (mode != "sqlserver")
            throw new InvalidOperationException(
                "LLM_LEDGER_MODE must be memory or sqlserver.");

        foreach (var userId in configuredBudgets.Keys)
            if (string.IsNullOrWhiteSpace(userId) || userId.Length > 100 ||
                userId.Any(char.IsControl) || userId != userId.TrimEnd())
                throw new InvalidOperationException(
                    "SQL mode requires canonical user IDs of at most 100 characters.");

        var connectionString = Environment.GetEnvironmentVariable(
            "LLM_SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Missing configuration: LLM_SQL_CONNECTION_STRING");

        var timeoutText = Environment.GetEnvironmentVariable(
            "LLM_SQL_COMMAND_TIMEOUT_SECONDS") ?? "30";
        if (!int.TryParse(timeoutText, NumberStyles.None,
                CultureInfo.InvariantCulture, out var timeout) ||
            timeout is < 1 or > 300)
            throw new InvalidOperationException(
                "LLM_SQL_COMMAND_TIMEOUT_SECONDS must be between 1 and 300.");

        // Resolve only the official provider, never a configurable assembly path.
        var factoryType = Type.GetType(
            "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
            throwOnError: false);
        var factory = factoryType?.GetField(
            "Instance", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as DbProviderFactory;
        if (factory is null)
            throw new InvalidOperationException(
                "SQL mode requires the official Microsoft.Data.SqlClient package " +
                "in the consuming application.");

        // SQL budgets must already be provisioned through the ledger schema.
        // Configured in-memory budgets never overwrite database balances.
        // Constructor selection does not validate connectivity or permissions.
        return new SqlSpendingLedger(factory, connectionString, timeout);
    }
}
