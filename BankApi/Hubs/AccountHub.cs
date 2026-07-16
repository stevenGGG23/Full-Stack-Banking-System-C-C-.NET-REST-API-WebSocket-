using Microsoft.AspNetCore.SignalR;

namespace BankApi.Hubs;

// Broadcast-only for now: every client receives every account's updates.
// Per-account subscription (via Groups) can be added once there's a client to drive it.
public class AccountHub : Hub
{
}
