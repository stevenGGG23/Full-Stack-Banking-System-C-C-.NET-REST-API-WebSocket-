var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5082");
var app = builder.Build();

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

app.Run();

// Type declarations go AFTER all top-level statements in C#
// A record is a lightweight class for holding data - auto-serialized to JSON
record Account(int AccountId, int UserId, string AccountType, decimal Balance);
