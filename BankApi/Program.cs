using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5082");
var app = builder.Build();

var connString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bank";

// Fake in-memory data for now (the database comes next)
var accounts = new List<Account>
{
    new Account(2, 1, "checking", 500.00m)
};

// GET / - simple health check
app.MapGet("/", () => Results.Ok(new { status = "ok", message = "Bank API is running" }));

// GET /api/accounts - list all accounts
app.MapGet("/api/accounts", () => accounts);

// GET /api/accounts/2 - one account by id
app.MapGet("/api/accounts/{id}", (int id) =>
{
    var account = accounts.FirstOrDefault(a => a.AccountId == id);
    return account is null ? Results.NotFound() : Results.Ok(account);
});

// POST /api/transactions/deposit - body: { "accountId": 2, "amount": 100.00 }
app.MapPost("/api/transactions/deposit", async (DepositRequest req) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var update = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance + @amt WHERE account_id = @id", conn, tx);
    update.Parameters.AddWithValue("amt", req.Amount);
    update.Parameters.AddWithValue("id", req.AccountId);
    var rows = await update.ExecuteNonQueryAsync();

    if (rows == 0)
    {
        await tx.RollbackAsync();
        return Results.NotFound(new { error = "Account not found" });
    }

    await using var insert = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount) VALUES (@id, 'deposit', @amt)", conn, tx);
    insert.Parameters.AddWithValue("id", req.AccountId);
    insert.Parameters.AddWithValue("amt", req.Amount);
    await insert.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    return Results.Ok(new { message = "Deposit successful" });
});

// POST /api/transactions/withdraw - body: { "accountId": 2, "amount": 100.00 }
app.MapPost("/api/transactions/withdraw", async (WithdrawRequest req) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var update = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance - @amt WHERE account_id = @id AND balance >= @amt", conn, tx);
    update.Parameters.AddWithValue("amt", req.Amount);
    update.Parameters.AddWithValue("id", req.AccountId);
    var rows = await update.ExecuteNonQueryAsync();

    if (rows == 0)
    {
        await using var exists = new NpgsqlCommand(
            "SELECT 1 FROM accounts WHERE account_id = @id", conn, tx);
        exists.Parameters.AddWithValue("id", req.AccountId);
        var found = await exists.ExecuteScalarAsync() is not null;

        await tx.RollbackAsync();
        return found
            ? Results.BadRequest(new { error = "Insufficient funds" })
            : Results.NotFound(new { error = "Account not found" });
    }

    await using var insert = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount) VALUES (@id, 'withdrawal', @amt)", conn, tx);
    insert.Parameters.AddWithValue("id", req.AccountId);
    insert.Parameters.AddWithValue("amt", req.Amount);
    await insert.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    return Results.Ok(new { message = "Withdrawal successful" });
});

// POST /api/transactions/transfer - body: { "fromAccountId": 2, "toAccountId": 3, "amount": 100.00 }
app.MapPost("/api/transactions/transfer", async (TransferRequest req) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });
    if (req.FromAccountId == req.ToAccountId)
        return Results.BadRequest(new { error = "Cannot transfer to the same account" });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var debit = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance - @amt WHERE account_id = @id AND balance >= @amt", conn, tx);
    debit.Parameters.AddWithValue("amt", req.Amount);
    debit.Parameters.AddWithValue("id", req.FromAccountId);
    var debitRows = await debit.ExecuteNonQueryAsync();

    if (debitRows == 0)
    {
        await using var exists = new NpgsqlCommand(
            "SELECT 1 FROM accounts WHERE account_id = @id", conn, tx);
        exists.Parameters.AddWithValue("id", req.FromAccountId);
        var found = await exists.ExecuteScalarAsync() is not null;

        await tx.RollbackAsync();
        return found
            ? Results.BadRequest(new { error = "Insufficient funds" })
            : Results.NotFound(new { error = "Source account not found" });
    }

    await using var credit = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance + @amt WHERE account_id = @id", conn, tx);
    credit.Parameters.AddWithValue("amt", req.Amount);
    credit.Parameters.AddWithValue("id", req.ToAccountId);
    var creditRows = await credit.ExecuteNonQueryAsync();

    if (creditRows == 0)
    {
        await tx.RollbackAsync();
        return Results.NotFound(new { error = "Destination account not found" });
    }

    await using var insertOut = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount) VALUES (@id, 'transfer_out', @amt)", conn, tx);
    insertOut.Parameters.AddWithValue("id", req.FromAccountId);
    insertOut.Parameters.AddWithValue("amt", req.Amount);
    await insertOut.ExecuteNonQueryAsync();

    await using var insertIn = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount) VALUES (@id, 'transfer_in', @amt)", conn, tx);
    insertIn.Parameters.AddWithValue("id", req.ToAccountId);
    insertIn.Parameters.AddWithValue("amt", req.Amount);
    await insertIn.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    return Results.Ok(new { message = "Transfer successful" });
});

app.Run();

// Type declarations go AFTER all top-level statements in C#
// A record is a lightweight class for holding data - auto-serialized to JSON
record Account(int AccountId, int UserId, string AccountType, decimal Balance);
record DepositRequest(int AccountId, decimal Amount);
record WithdrawRequest(int AccountId, decimal Amount);
record TransferRequest(int FromAccountId, int ToAccountId, decimal Amount);
