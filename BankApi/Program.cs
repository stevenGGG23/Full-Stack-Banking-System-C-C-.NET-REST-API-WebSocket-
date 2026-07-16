using BankApi.Hubs;
using BankApi.Services;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5082");

var engineUrl = builder.Configuration["EngineUrl"] ?? "http://localhost:8081";
builder.Services.AddHttpClient<EngineClient>(client => client.BaseAddress = new Uri(engineUrl));
builder.Services.AddSignalR();

var app = builder.Build();

app.MapHub<AccountHub>("/hubs/accounts");

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
app.MapPost("/api/transactions/deposit", async (DepositRequest req, EngineClient engine, IHubContext<AccountHub> hub) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });

    EngineClient.ValidationResult validation;
    try
    {
        // currentBalance is unused by the engine for deposits, so 0 is a safe placeholder.
        validation = await engine.ValidateAsync("deposit", req.Amount, currentBalance: 0);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { error = "Transaction engine unavailable" }, statusCode: 503);
    }

    if (!validation.Approved)
        return Results.BadRequest(new { error = validation.Reason });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var update = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance + @amt WHERE account_id = @id RETURNING balance", conn, tx);
    update.Parameters.AddWithValue("amt", req.Amount);
    update.Parameters.AddWithValue("id", req.AccountId);
    var newBalance = await update.ExecuteScalarAsync();

    if (newBalance is null)
    {
        await tx.RollbackAsync();
        return Results.NotFound(new { error = "Account not found" });
    }

    await using var insert = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount, fee) VALUES (@id, 'deposit', @amt, @fee)", conn, tx);
    insert.Parameters.AddWithValue("id", req.AccountId);
    insert.Parameters.AddWithValue("amt", req.Amount);
    insert.Parameters.AddWithValue("fee", validation.Fee);
    await insert.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    await hub.Clients.All.SendAsync("BalanceUpdated", new { accountId = req.AccountId, balance = (decimal)newBalance });
    return Results.Ok(new { message = "Deposit successful" });
});

// POST /api/transactions/withdraw - body: { "accountId": 2, "amount": 100.00 }
app.MapPost("/api/transactions/withdraw", async (WithdrawRequest req, EngineClient engine, IHubContext<AccountHub> hub) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    decimal currentBalance;
    await using (var select = new NpgsqlCommand(
        "SELECT balance FROM accounts WHERE account_id = @id FOR UPDATE", conn, tx))
    {
        select.Parameters.AddWithValue("id", req.AccountId);
        var result = await select.ExecuteScalarAsync();
        if (result is null)
        {
            await tx.RollbackAsync();
            return Results.NotFound(new { error = "Account not found" });
        }
        currentBalance = (decimal)result;
    }

    EngineClient.ValidationResult validation;
    try
    {
        validation = await engine.ValidateAsync("withdrawal", req.Amount, currentBalance);
    }
    catch (HttpRequestException)
    {
        await tx.RollbackAsync();
        return Results.Json(new { error = "Transaction engine unavailable" }, statusCode: 503);
    }

    if (!validation.Approved)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = validation.Reason });
    }

    var total = req.Amount + validation.Fee;
    await using var update = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance - @total WHERE account_id = @id AND balance >= @total RETURNING balance", conn, tx);
    update.Parameters.AddWithValue("total", total);
    update.Parameters.AddWithValue("id", req.AccountId);
    var newBalance = await update.ExecuteScalarAsync();

    if (newBalance is null)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "Insufficient funds" });
    }

    await using var insert = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount, fee) VALUES (@id, 'withdrawal', @amt, @fee)", conn, tx);
    insert.Parameters.AddWithValue("id", req.AccountId);
    insert.Parameters.AddWithValue("amt", req.Amount);
    insert.Parameters.AddWithValue("fee", validation.Fee);
    await insert.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    await hub.Clients.All.SendAsync("BalanceUpdated", new { accountId = req.AccountId, balance = (decimal)newBalance });
    return Results.Ok(new { message = "Withdrawal successful", fee = validation.Fee });
});

// POST /api/transactions/transfer - body: { "fromAccountId": 2, "toAccountId": 3, "amount": 100.00 }
app.MapPost("/api/transactions/transfer", async (TransferRequest req, EngineClient engine, IHubContext<AccountHub> hub) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });
    if (req.FromAccountId == req.ToAccountId)
        return Results.BadRequest(new { error = "Cannot transfer to the same account" });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    decimal currentBalance;
    await using (var select = new NpgsqlCommand(
        "SELECT balance FROM accounts WHERE account_id = @id FOR UPDATE", conn, tx))
    {
        select.Parameters.AddWithValue("id", req.FromAccountId);
        var result = await select.ExecuteScalarAsync();
        if (result is null)
        {
            await tx.RollbackAsync();
            return Results.NotFound(new { error = "Source account not found" });
        }
        currentBalance = (decimal)result;
    }

    EngineClient.ValidationResult validation;
    try
    {
        validation = await engine.ValidateAsync("transfer_out", req.Amount, currentBalance);
    }
    catch (HttpRequestException)
    {
        await tx.RollbackAsync();
        return Results.Json(new { error = "Transaction engine unavailable" }, statusCode: 503);
    }

    if (!validation.Approved)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = validation.Reason });
    }

    var total = req.Amount + validation.Fee;
    await using var debit = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance - @total WHERE account_id = @id AND balance >= @total RETURNING balance", conn, tx);
    debit.Parameters.AddWithValue("total", total);
    debit.Parameters.AddWithValue("id", req.FromAccountId);
    var fromBalance = await debit.ExecuteScalarAsync();

    if (fromBalance is null)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "Insufficient funds" });
    }

    await using var credit = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance + @amt WHERE account_id = @id RETURNING balance", conn, tx);
    credit.Parameters.AddWithValue("amt", req.Amount);
    credit.Parameters.AddWithValue("id", req.ToAccountId);
    var toBalance = await credit.ExecuteScalarAsync();

    if (toBalance is null)
    {
        await tx.RollbackAsync();
        return Results.NotFound(new { error = "Destination account not found" });
    }

    await using var insertOut = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount, fee) VALUES (@id, 'transfer_out', @amt, @fee)", conn, tx);
    insertOut.Parameters.AddWithValue("id", req.FromAccountId);
    insertOut.Parameters.AddWithValue("amt", req.Amount);
    insertOut.Parameters.AddWithValue("fee", validation.Fee);
    await insertOut.ExecuteNonQueryAsync();

    await using var insertIn = new NpgsqlCommand(
        "INSERT INTO transactions (account_id, transaction_type, amount, fee) VALUES (@id, 'transfer_in', @amt, 0)", conn, tx);
    insertIn.Parameters.AddWithValue("id", req.ToAccountId);
    insertIn.Parameters.AddWithValue("amt", req.Amount);
    await insertIn.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    await hub.Clients.All.SendAsync("BalanceUpdated", new { accountId = req.FromAccountId, balance = (decimal)fromBalance });
    await hub.Clients.All.SendAsync("BalanceUpdated", new { accountId = req.ToAccountId, balance = (decimal)toBalance });
    return Results.Ok(new { message = "Transfer successful", fee = validation.Fee });
});

app.Run();

// Type declarations go AFTER all top-level statements in C#
// A record is a lightweight class for holding data - auto-serialized to JSON
record Account(int AccountId, int UserId, string AccountType, decimal Balance);
record DepositRequest(int AccountId, decimal Amount);
record WithdrawRequest(int AccountId, decimal Amount);
record TransferRequest(int FromAccountId, int ToAccountId, decimal Amount);
