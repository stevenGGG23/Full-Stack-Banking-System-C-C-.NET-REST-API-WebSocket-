# Full-Stack Banking System (C++, C#, ASP.NET Core, PostgreSQL, REST, WebSockets)

A full-stack banking system built as a multi-service architecture. An ASP.NET
Core API handles auth, persistence, and orchestration; a C++ service
validates every transaction and computes fees; PostgreSQL is the system of
record; and a SignalR hub pushes live, per-account balance updates to a
small browser frontend. Services communicate over REST.

## Architecture

```
┌──────────────┐   HTTP/REST    ┌────────────────────┐   HTTP/REST   ┌──────────────────┐
│   Browser    │ ─────────────► │  BankApi (C#)      │ ────────────► │  Engine (C++)    │
│  wwwroot/    │ ◄───────────── │  ASP.NET Core /     │ ◄──────────── │  cpp-httplib     │
│  index.html  │   WebSockets   │  .NET 10 minimal   │               │  fee/limit       │
│              │   (SignalR,    │  API + JWT auth    │               │  validation      │
│              │   JWT in query)│                     │               │                  │
└──────────────┘                └─────────┬──────────┘               └──────────────────┘
                                          │ Npgsql (NpgsqlDataSource)
                                          ▼
                                ┌────────────────────┐
                                │  PostgreSQL        │
                                │  users / accounts  │
                                │  / transactions    │
                                └────────────────────┘
```

| Layer | Technology | Responsibility |
|---|---|---|
| Database | PostgreSQL | Persistent storage: users, accounts, transaction ledger |
| Application API | C# / .NET 10, ASP.NET Core minimal APIs | JWT auth, ownership-scoped REST endpoints, database access, orchestration |
| Processing engine | C++17, cpp-httplib, nlohmann-json | Transaction validation, fee calculation, business-rule enforcement |
| Real-time layer | SignalR (WebSockets), JWT-authenticated | Pushes balance updates to clients subscribed to their own accounts |
| Frontend | Vanilla HTML/JS, served from `BankApi/wwwroot` | Login/register, account dashboard, deposit/withdraw/transfer, live balances |
| Inter-service communication | REST over HTTP | Contract between the API and the processing engine |

## Features

- Registration and login with bcrypt-hashed passwords and JWT bearer tokens
- Every account-scoped endpoint checks that the caller actually owns the
  account being read or acted on (returns `404`, not `403`, so a caller can't
  tell "not yours" from "doesn't exist")
- Deposits, withdrawals, and account-to-account transfers, all validated by
  the C++ engine (balance checks, fee calculation) before anything commits
- Immutable transaction ledger — every balance change is recorded with type,
  amount, fee, and timestamp; balances are never edited outside a transaction
- Live, per-account balance updates pushed over an authenticated SignalR hub
  (clients only ever hear about accounts they've proven they own), no polling
- A minimal browser dashboard to exercise the whole flow end to end

## Project Structure

```
.
├── database/
│   └── schema.sql              # users / accounts / transactions tables
├── BankApi/                    # ASP.NET Core minimal API + SignalR hub
│   ├── Program.cs              # all REST endpoints, auth wiring, DI setup
│   ├── Hubs/
│   │   └── AccountHub.cs       # per-account SignalR groups, ownership-checked subscribe
│   ├── Services/
│   │   ├── EngineClient.cs     # HTTP client for the C++ engine's /validate
│   │   └── TokenService.cs     # JWT issuance
│   └── wwwroot/
│       └── index.html          # login/register + dashboard, vanilla JS
├── engine/                      # C++ transaction-validation service
│   ├── CMakeLists.txt
│   └── src/main.cpp
└── README.md
```

There's no MVC `Controllers/`/`Models/` split — the API is built with ASP.NET
Core minimal APIs, so routes live directly in `Program.cs`.

## API Reference

### Auth

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/auth/register` | `{username, email, password}` → creates the user, opens a default `checking` account, returns `{token, accountId}`. `409` on duplicate username/email. |
| POST | `/api/auth/login` | `{username, password}` → returns `{token}`. `401` on any failure. |

### Accounts *(require `Authorization: Bearer <token>`)*

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/accounts` | `{accountType}` → opens a new account owned by the caller |
| GET | `/api/accounts` | Lists only the caller's own accounts |
| GET | `/api/accounts/{id}` | One account — `404` if it doesn't exist or isn't the caller's |

### Transactions *(require `Authorization: Bearer <token>`)*

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/transactions/deposit` | `{accountId, amount}` |
| POST | `/api/transactions/withdraw` | `{accountId, amount}` — engine-validated; 1% fee on amounts over $500 |
| POST | `/api/transactions/transfer` | `{fromAccountId, toAccountId, amount}` — engine-validated; flat $1 fee; the recipient account doesn't need to belong to the caller |

### Real-time

| Protocol | Endpoint | Description |
|---|---|---|
| WebSocket | `/hubs/accounts` | JWT-authenticated (token passed via `accessTokenFactory`/query string). Call `SubscribeToAccount(accountId)` — rejected unless the caller owns that account — then listen for `BalanceUpdated` events. |

### Engine (internal — not exposed to the browser)

| Method | Endpoint | Description |
|---|---|---|
| GET | `/health` | Liveness check |
| POST | `/validate` | `{transactionType, amount, currentBalance}` → `{approved, fee, reason}` |

### Misc

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/health` | BankApi liveness check |
| GET | `/` | Serves the frontend (`wwwroot/index.html`) |

## Database Schema

Three core tables (`database/schema.sql`):

- **users** — username/email/bcrypt password hash
- **accounts** — one or more per user; holds current balance and account type
- **transactions** — append-only ledger; every balance change is recorded
  with type, amount, fee, and timestamp

Balances are never edited directly outside a validated, committed transaction.

## Getting Started

### Run in GitHub Codespaces / a Linux dev container

1. Verify the toolchain:

```bash
dotnet --version
g++ --version
cmake --version
psql --version
```

2. Install the engine's two dependencies (both apt-installable, no vendoring
   needed):

```bash
sudo apt-get update && sudo apt-get install -y libcpp-httplib-dev nlohmann-json3-dev
```

3. Make sure PostgreSQL is running and the schema is loaded:

```bash
sudo service postgresql start
sudo su - postgres -c "createdb bank"   # skip if it already exists
sudo su - postgres -c "psql -f database/schema.sql"
```

   (`sudo -u postgres ...` will hang on a password prompt that can never be
   satisfied on this box's sudoers config — use `sudo su - postgres -c "..."`
   instead, which goes through root first.)

4. Build and run the C++ engine:

```bash
cd engine && cmake -B build && cmake --build build && ./build/engine
```

5. In a second terminal, run the API:

```bash
cd BankApi && dotnet run
```

6. Open `http://localhost:5082` (via the Codespaces **Ports** tab if remote)
   — register a user and use the dashboard. Registration auto-opens a
   `checking` account so there's something to deposit into immediately.

## Design Notes

- **Raw Npgsql, not an ORM.** Every query is explicit SQL, matching the
  hand-written schema — there's no EF Core migration layer or change-tracking
  magic between the code and the tables.
- **Validation before commit.** No transaction reaches the database until the
  C++ engine approves it and returns a fee; a rejection is returned to the
  client with a reason and nothing is persisted. If the engine is unreachable,
  the request fails with `503` rather than committing blind.
- **JWT over cookie sessions.** Tokens are stateless and work for non-browser
  clients too, not just the bundled frontend — a deliberate fit for something
  billed as a REST API.
- **Ownership checked server-side, always.** Every account-scoped SQL query
  filters on `user_id = @callerId` (from the JWT claim) directly, and the
  SignalR hub re-checks ownership against the database before adding a
  connection to an account's group — a client can't just claim an account ID
  and start listening.
- **A shared `NpgsqlDataSource` singleton, not per-request connections.** The
  SignalR hub is instantiated by DI per connection and can't reach a
  connection string closed over by top-level endpoint lambdas, so the whole
  app (endpoints and hub alike) shares one pooled `NpgsqlDataSource` — also
  Npgsql's own recommended pattern over ad-hoc `new NpgsqlConnection`.
- **The engine is a real separate process**, not a P/Invoke library — it's
  reached over HTTP like any other service, so "C++ handles validation" means
  an actual network boundary, not just C++ code linked into the C# process.
- **cpp-httplib over raw sockets or Boost.Beast.** Both apt-installable
  (`libcpp-httplib-dev`, `nlohmann-json3-dev`), letting the engine's code
  focus on the fee/limit logic instead of hand-rolling HTTP parsing.

## Tech Stack

`C#` · `.NET 10 / ASP.NET Core (minimal APIs)` · `JWT` · `BCrypt.Net` ·
`C++17` · `cpp-httplib` · `nlohmann-json` · `CMake` · `PostgreSQL` · `Npgsql` ·
`SignalR` · `REST` · `GitHub Codespaces`

## License

MIT
