using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace LlmIntegration;

// Provider-neutral ADO.NET adapter. Supply Microsoft's SQL Server factory
// from the consuming application. No provider package is referenced here.
// SQL balances and holds are durable. Local request/completion context is not.
public sealed class SqlSpendingLedger : ISpendingLedger
{
    private const decimal MaximumAmount = 99999999999.99999999m;
    private readonly DbProviderFactory factory;
    private readonly string connectionString;
    private readonly int commandTimeoutSeconds;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();

    private sealed record Completion(
        decimal Confirmed, decimal Uncertain, string Outcome);

    private sealed class Entry(
        string userId, Guid requestId, decimal reserved)
    {
        public string UserId { get; } = userId;
        public Guid RequestId { get; } = requestId;
        public decimal Reserved { get; } = reserved;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public bool Admitted { get; set; }
        public Completion? Pending { get; set; }
        public CostRecord? Completed { get; set; }
    }

    public SqlSpendingLedger(
        DbProviderFactory factory, string connectionString,
        int commandTimeoutSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (commandTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds));
        this.factory = factory;
        this.connectionString = connectionString;
        this.commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public bool TryReserve(string userId, Guid requestId, decimal exposureUsd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > 100 || userId.Any(char.IsControl) ||
            userId != userId.TrimEnd())
            throw new ArgumentException(
                "Use a canonical SQL user ID of at most 100 characters.",
                nameof(userId));
        if (requestId == Guid.Empty)
            throw new ArgumentException(
                "A request ID is required.", nameof(requestId));
        if (exposureUsd <= 0)
            throw new ArgumentOutOfRangeException(nameof(exposureUsd));
        var reserved = RoundExposureUp(exposureUsd);

        lock (gate)
        {
            if (entries.ContainsKey(requestId))
                throw new InvalidOperationException(
                    "Request ID already used by this adapter.");

            // Retain the ID even after an ambiguous database outcome.
            // Such an outcome must never authorize provider dispatch.
            var entry = new Entry(userId, requestId, reserved);
            entries.Add(requestId, entry);
            try
            {
                var replay = Execute(
                    "dbo.LlmReserve", entry, command =>
                        AddMoney(command, "@ExposureUsd", reserved));
                if (replay)
                    throw new InvalidOperationException(
                        "Reservation already exists; dispatch is not authorized.");
                entry.Admitted = true;
                return true;
            }
            catch (DbException exception) when (
                SqlErrorNumber(exception) is 51002 or 51004)
            {
                // Only known SQL admission rejections become false.
                // Connection errors, timeouts and other errors propagate.
                return false;
            }
        }
    }

    public CostRecord Complete(
        Guid requestId, decimal confirmedUsd, decimal uncertainUsd,
        string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (outcome.Length > 160 || outcome.Any(char.IsControl))
            throw new ArgumentException(
                "Outcome must contain at most 160 non-control characters.",
                nameof(outcome));
        var confirmed = RoundExposureUp(confirmedUsd);
        var uncertain = RoundExposureUp(uncertainUsd);
        var requested = new Completion(confirmed, uncertain, outcome);

        lock (gate)
        {
            if (!entries.TryGetValue(requestId, out var entry) || !entry.Admitted)
                throw new InvalidOperationException(
                    "No locally admitted reservation exists for this request.");
            if (entry.Pending is not null && entry.Pending != requested)
                throw new InvalidOperationException(
                    "Completion retries require identical accounting arguments.");
            entry.Pending = requested;
            if (entry.Completed is not null) return entry.Completed;

            // Check the entire plan before confirming OR releasing anything.
            // Do not clamp external observations or free an overrun's hold.
            if (confirmed + uncertain > entry.Reserved)
                throw new SqlLedgerReconciliationRequiredException(
                    requestId, entry.UserId, entry.Reserved,
                    confirmedUsd, uncertainUsd, outcome);

            // Each call is its own transaction, as required by the SQL script.
            // If confirmation commits but release fails, replaying Complete
            // uses the same IDs; confirmation is not applied twice.
            if (confirmed > 0)
                Execute(
                    "dbo.LlmConfirm", entry, command =>
                    {
                        Add(command, "@OperationId", DbType.Guid,
                            OperationId(requestId, "confirm"));
                        AddMoney(command, "@AmountUsd", confirmed);
                        Add(command, "@Reason", DbType.String,
                            "gateway-confirm: " + outcome, 200);
                    });

            var release = entry.Reserved - confirmed - uncertain;
            if (release > 0)
                Execute(
                    "dbo.LlmRelease", entry, command =>
                    {
                        Add(command, "@OperationId", DbType.Guid,
                            OperationId(requestId, "release"));
                        AddMoney(command, "@AmountUsd", release);
                        Add(command, "@Reason", DbType.String,
                            "gateway-unused: " + outcome, 200);
                        Add(command, "@ProvenUnused", DbType.Boolean, true);
                    });

            entry.Completed = new CostRecord(
                requestId, entry.UserId, entry.Reserved, confirmed, uncertain,
                outcome, entry.StartedAt, DateTimeOffset.UtcNow, false);
            return entry.Completed;
        }
    }

    private bool Execute(
        string procedure, Entry entry, Action<DbCommand> configure)
    {
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException(
                "Ledger calls cannot run inside an ambient transaction.");
        using var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException(
                "The configured provider did not create a connection.");
        connection.ConnectionString = connectionString;
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = procedure;
        command.CommandTimeout = commandTimeoutSeconds;
        Add(command, "@UserId", DbType.String, entry.UserId, 100);
        Add(command, "@RequestId", DbType.Guid, entry.RequestId);
        configure(command);
        var result = command.ExecuteScalar();
        // SQL returns a bit column named AlreadyApplied.
        if (result is not bool replay)
            throw new InvalidOperationException(
                "Ledger procedure returned an unexpected result; reconcile before dispatch.");
        return replay;
    }

    private static void Add(
        DbCommand command, string name, DbType type, object value,
        int? size = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (size.HasValue) parameter.Size = size.Value;
        command.Parameters.Add(parameter);
    }

    private static void AddMoney(
        DbCommand command, string name, decimal value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Decimal;
        parameter.Precision = 19;
        parameter.Scale = 8;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static decimal RoundExposureUp(decimal value)
    {
        if (value < 0 || value > MaximumAmount)
            throw new ArgumentOutOfRangeException(nameof(value));
        return decimal.Ceiling(value * 100000000m) / 100000000m;
    }

    private static Guid OperationId(Guid requestId, string kind)
    {
        var input = Encoding.UTF8.GetBytes(
            "llm-sql-ledger/v1/" + requestId.ToString("D") + "/" + kind);
        var digest = SHA256.HashData(input);
        return new Guid(digest.AsSpan(0, 16));
    }

    private static int? SqlErrorNumber(DbException exception)
    {
        // Microsoft.Data.SqlClient.SqlException.Number, without a package
        // dependency in this framework-only source project.
        return exception.GetType().GetProperty("Number")?.GetValue(exception)
            is int number ? number : null;
    }
}

// Caller must durably record this observation and suspend further provider
// dispatch for the user until reconciliation. This adapter leaves the hold
// intact but does not implement that application-wide suspension workflow.
public sealed class SqlLedgerReconciliationRequiredException : Exception
{
    public Guid RequestId { get; }
    public string UserId { get; }
    public decimal ReservedUsd { get; }
    public decimal ObservedConfirmedUsd { get; }
    public decimal ObservedUncertainUsd { get; }
    public string Outcome { get; }

    public SqlLedgerReconciliationRequiredException(
        Guid requestId, string userId, decimal reservedUsd,
        decimal confirmedUsd, decimal uncertainUsd, string outcome)
        : base("Observed exposure exceeds the reservation; hold preserved and reconciliation required.")
    {
        RequestId = requestId;
        UserId = userId;
        ReservedUsd = reservedUsd;
        ObservedConfirmedUsd = confirmedUsd;
        ObservedUncertainUsd = uncertainUsd;
        Outcome = outcome;
    }
}
