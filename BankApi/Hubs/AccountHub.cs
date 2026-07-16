using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace BankApi.Hubs;

[Authorize]
public class AccountHub(NpgsqlDataSource db) : Hub
{
    public async Task SubscribeToAccount(int accountId)
    {
        if (!await OwnsAccount(accountId))
            throw new HubException("Account not found");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"account-{accountId}");
    }

    public async Task UnsubscribeFromAccount(int accountId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"account-{accountId}");
    }

    private async Task<bool> OwnsAccount(int accountId)
    {
        var callerId = int.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

        await using var conn = await db.OpenConnectionAsync();
        await using var select = new NpgsqlCommand(
            "SELECT 1 FROM accounts WHERE account_id = @id AND user_id = @userId", conn);
        select.Parameters.AddWithValue("id", accountId);
        select.Parameters.AddWithValue("userId", callerId);

        return await select.ExecuteScalarAsync() is not null;
    }
}
