using System.Security.Claims;
using BankApi.Hubs;
using BankApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5082");

var connString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bank";
builder.Services.AddSingleton(NpgsqlDataSource.Create(connString));

var engineUrl = builder.Configuration["EngineUrl"] ?? "http://localhost:8081";
builder.Services.AddHttpClient<EngineClient>(client => client.BaseAddress = new Uri(engineUrl));
builder.Services.AddSignalR();
builder.Services.AddSingleton<TokenService>();

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtSection["Key"]!));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwtKey,
            ValidateLifetime = true,
        };
        // Browser SignalR clients can't set an Authorization header on the
        // WebSocket handshake, so the JS client sends the token as a query
        // string param instead (via accessTokenFactory) - accept it there too.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<AccountHub>("/hubs/accounts").RequireAuthorization();

// GET /api/health - simple health check ("/" now serves the frontend, see wwwroot/index.html)
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", message = "Bank API is running" }));

// POST /api/auth/register - body: { "username": "...", "email": "...", "password": "..." }
app.MapPost("/api/auth/register", async (RegisterRequest req, NpgsqlDataSource db, TokenService tokens) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required" });

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);

    int userId;
    try
    {
        await using var insertUser = new NpgsqlCommand(
            "INSERT INTO users (username, email, password_hash) VALUES (@username, @email, @hash) RETURNING user_id",
            conn, tx);
        insertUser.Parameters.AddWithValue("username", req.Username);
        insertUser.Parameters.AddWithValue("email", req.Email);
        insertUser.Parameters.AddWithValue("hash", passwordHash);
        userId = (int)(await insertUser.ExecuteScalarAsync())!;
    }
    catch (PostgresException ex) when (ex.SqlState == "23505")
    {
        await tx.RollbackAsync();
        return Results.Conflict(new { error = "Username or email already in use" });
    }

    await using var insertAccount = new NpgsqlCommand(
        "INSERT INTO accounts (user_id, account_type, balance) VALUES (@userId, 'checking', 0.00) RETURNING account_id",
        conn, tx);
    insertAccount.Parameters.AddWithValue("userId", userId);
    var accountId = (int)(await insertAccount.ExecuteScalarAsync())!;

    await tx.CommitAsync();

    var token = tokens.GenerateToken(userId, req.Username);
    return Results.Ok(new { token, accountId });
});

// POST /api/auth/login - body: { "username": "...", "password": "..." }
app.MapPost("/api/auth/login", async (LoginRequest req, NpgsqlDataSource db, TokenService tokens) =>
{
    await using var conn = await db.OpenConnectionAsync();
    await using var select = new NpgsqlCommand(
        "SELECT user_id, password_hash FROM users WHERE username = @username", conn);
    select.Parameters.AddWithValue("username", req.Username);

    await using var reader = await select.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.Unauthorized();

    var userId = reader.GetInt32(0);
    var passwordHash = reader.GetString(1);
    await reader.CloseAsync();

    if (!BCrypt.Net.BCrypt.Verify(req.Password, passwordHash))
        return Results.Unauthorized();

    var token = tokens.GenerateToken(userId, req.Username);
    return Results.Ok(new { token });
});

// POST /api/accounts - body: { "accountType": "checking" }
app.MapPost("/api/accounts", async (CreateAccountRequest req, NpgsqlDataSource db, ClaimsPrincipal user) =>
{
    var callerId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenConnectionAsync();
    await using var insert = new NpgsqlCommand(
        "INSERT INTO accounts (user_id, account_type, balance) VALUES (@userId, @type, 0.00) RETURNING account_id, user_id, account_type, balance",
        conn);
    insert.Parameters.AddWithValue("userId", callerId);
    insert.Parameters.AddWithValue("type", req.AccountType);

    await using var reader = await insert.ExecuteReaderAsync();
    await reader.ReadAsync();
    var account = new Account(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDecimal(3));
    return Results.Ok(account);
}).RequireAuthorization();

// GET /api/accounts - the caller's own accounts
app.MapGet("/api/accounts", async (NpgsqlDataSource db, ClaimsPrincipal user) =>
{
    var callerId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenConnectionAsync();
    await using var select = new NpgsqlCommand(
        "SELECT account_id, user_id, account_type, balance FROM accounts WHERE user_id = @userId", conn);
    select.Parameters.AddWithValue("userId", callerId);

    var results = new List<Account>();
    await using var reader = await select.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        results.Add(new Account(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDecimal(3)));

    return Results.Ok(results);
}).RequireAuthorization();

// GET /api/accounts/{id} - one account by id, only if owned by the caller
app.MapGet("/api/accounts/{id}", async (int id, NpgsqlDataSource db, ClaimsPrincipal user) =>
{
    var callerId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenConnectionAsync();
    await using var select = new NpgsqlCommand(
        "SELECT account_id, user_id, account_type, balance FROM accounts WHERE account_id = @id AND user_id = @userId", conn);
    select.Parameters.AddWithValue("id", id);
    select.Parameters.AddWithValue("userId", callerId);

    await using var reader = await select.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.NotFound();

    var account = new Account(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDecimal(3));
    return Results.Ok(account);
}).RequireAuthorization();

// POST /api/transactions/deposit - body: { "accountId": 2, "amount": 100.00 }
app.MapPost("/api/transactions/deposit", async (DepositRequest req, EngineClient engine, IHubContext<AccountHub> hub, NpgsqlDataSource db, ClaimsPrincipal user) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });

    var callerId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

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

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var update = new NpgsqlCommand(
        "UPDATE accounts SET balance = balance + @amt WHERE account_id = @id AND user_id = @userId RETURNING balance", conn, tx);
    update.Parameters.AddWithValue("amt", req.Amount);
    update.Parameters.AddWithValue("id", req.AccountId);
    update.Parameters.AddWithValue("userId", callerId);
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
    await hub.Clients.Group($"account-{req.AccountId}").SendAsync("BalanceUpdated", new { accountId = req.AccountId, balance = (decimal)newBalance });
    return Results.Ok(new { message = "Deposit successful" });
}).RequireAuthorization();

// POST /api/transactions/withdraw - body: { "accountId": 2, "amount": 100.00 }
app.MapPost("/api/transactions/withdraw", async (WithdrawRequest req, EngineClient engine, IHubContext<AccountHub> hub, NpgsqlDataSource db, ClaimsPrincipal user) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });

    var callerId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    decimal currentBalance;
    await using (var select = new NpgsqlCommand(
        "SELECT balance FROM accounts WHERE account_id = @id AND user_id = @userId FOR UPDATE", conn, tx))
    {
        select.Parameters.AddWithValue("id", req.AccountId);
        select.Parameters.AddWithValue("userId", callerId);
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
    await hub.Clients.Group($"account-{req.AccountId}").SendAsync("BalanceUpdated", new { accountId = req.AccountId, balance = (decimal)newBalance });
    return Results.Ok(new { message = "Withdrawal successful", fee = validation.Fee });
}).RequireAuthorization();

// POST /api/transactions/transfer - body: { "fromAccountId": 2, "toAccountId": 3, "amount": 100.00 }
app.MapPost("/api/transactions/transfer", async (TransferRequest req, EngineClient engine, IHubContext<AccountHub> hub, NpgsqlDataSource db, ClaimsPrincipal user) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be positive" });
    if (req.FromAccountId == req.ToAccountId)
        return Results.BadRequest(new { error = "Cannot transfer to the same account" });

    var callerId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    decimal currentBalance;
    await using (var select = new NpgsqlCommand(
        "SELECT balance FROM accounts WHERE account_id = @id AND user_id = @userId FOR UPDATE", conn, tx))
    {
        select.Parameters.AddWithValue("id", req.FromAccountId);
        select.Parameters.AddWithValue("userId", callerId);
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
    await hub.Clients.Group($"account-{req.FromAccountId}").SendAsync("BalanceUpdated", new { accountId = req.FromAccountId, balance = (decimal)fromBalance });
    await hub.Clients.Group($"account-{req.ToAccountId}").SendAsync("BalanceUpdated", new { accountId = req.ToAccountId, balance = (decimal)toBalance });
    return Results.Ok(new { message = "Transfer successful", fee = validation.Fee });
}).RequireAuthorization();

app.Run();

// Type declarations go AFTER all top-level statements in C#
// A record is a lightweight class for holding data - auto-serialized to JSON
record Account(int AccountId, int UserId, string AccountType, decimal Balance);
record DepositRequest(int AccountId, decimal Amount);
record WithdrawRequest(int AccountId, decimal Amount);
record TransferRequest(int FromAccountId, int ToAccountId, decimal Amount);
record RegisterRequest(string Username, string Email, string Password);
record LoginRequest(string Username, string Password);
record CreateAccountRequest(string AccountType);
