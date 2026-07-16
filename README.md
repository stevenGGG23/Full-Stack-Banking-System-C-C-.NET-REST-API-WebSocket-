# Full-Stack Banking System (C++, C#, ASP.NET Core, PostgreSQL, REST, WebSockets)

A full-stack banking system built as a multi-service architecture. An ASP.NET Core API serves as the primary application layer, a C++ service handles transaction processing, PostgreSQL provides persistent storage, and SignalR (WebSockets) delivers real-time balance updates to connected clients. Services communicate over REST.

## Architecture

```
┌──────────────┐   HTTP/REST    ┌────────────────────┐   HTTP/REST   ┌──────────────────┐
│   Client     │ ─────────────► │  BankApi (C#)      │ ────────────► │  Engine (C++)    │
│  (Web UI)    │ ◄───────────── │  ASP.NET Core 8    │ ◄──────────── │  Transaction     │
│              │   WebSockets   │                    │               │  Processor       │
└──────────────┘   (SignalR)    └─────────┬──────────┘               └──────────────────┘
                                          │ Npgsql
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
| Application API | C# / ASP.NET Core 8 | Authentication, REST endpoints, database access, orchestration |
| Processing engine | C++ | Transaction validation, fee calculation, business-rule enforcement |
| Real-time layer | SignalR (WebSockets) | Pushes balance and transaction updates to connected clients |
| Inter-service communication | REST over HTTP | Contract between the API and the processing engine |

## Features

- Account creation and management with user authentication
- Deposits, withdrawals, and account-to-account transfers
- All transactions validated by the C++ engine (balance checks, limits, fee application) before commit
- Immutable transaction ledger with full history per account
- Live balance updates pushed to clients over WebSockets, no polling
- Clean REST API surface, testable independently of any frontend

## Project Structure

```
learning-bank/
├── .devcontainer/
│   └── devcontainer.json     # Codespaces environment definition
├── database/
│   ├── schema.sql            # Table definitions
│   └── seed.sql              # Sample data
├── BankApi/                  # ASP.NET Core REST API + SignalR hub
│   ├── Controllers/
│   ├── Hubs/
│   ├── Models/
│   └── Services/             # Includes the HTTP client for the C++ engine
├── engine/                   # C++ transaction-processing service
│   ├── src/
│   └── CMakeLists.txt
└── README.md
```

## API Reference

### Accounts

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/accounts` | Create a new account |
| GET | `/api/accounts/{id}` | Retrieve account details |
| GET | `/api/accounts/{id}/balance` | Retrieve current balance |
| GET | `/api/accounts/{id}/transactions` | Retrieve transaction history |

### Transactions

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/transactions/deposit` | Deposit funds |
| POST | `/api/transactions/withdraw` | Withdraw funds (engine-validated) |
| POST | `/api/transactions/transfer` | Transfer between accounts (engine-validated) |

### Real-time

| Protocol | Endpoint | Description |
|---|---|---|
| WebSocket | `/hubs/accounts` | SignalR hub; clients subscribe to live balance/transaction events |

### Engine (internal)

| Method | Endpoint | Description |
|---|---|---|
| POST | `/validate` | Validates a proposed transaction and returns applied fees or a rejection reason |

## Database Schema

Three core tables:

- **users** — identity and credentials
- **accounts** — one or more per user; holds current balance and account type
- **transactions** — append-only ledger; every balance change is recorded with type, amount, fee, and timestamp

Balances are never edited directly; they are derived from validated, committed transactions.

## Getting Started

### Run in GitHub Codespaces

The repository includes a devcontainer definition that provisions the full toolchain (.NET 8 SDK, g++/CMake, PostgreSQL).

1. Open the repository on GitHub, select **Code → Codespaces → Create codespace on main**.
2. Once the container builds, verify the environment:

```bash
dotnet --version
g++ --version
psql --version
sudo service postgresql status
```

3. Start PostgreSQL if it is not running (required after every Codespace restart):

```bash
sudo service postgresql start
```

4. Initialize the database:

```bash
sudo su - postgres -c "psql -f database/schema.sql"
sudo su - postgres -c "psql -f database/seed.sql"
```

5. Build and run the C++ engine:

```bash
cd engine && cmake -B build && cmake --build build && ./build/engine
```

6. In a second terminal, run the API:

```bash
cd BankApi && dotnet run
```

The API is exposed through the Codespaces **Ports** tab (default port 5082).

## Design Notes

- **Separation of concerns.** The C# layer owns persistence and client communication; the C++ layer owns business rules. Neither service touches the other's domain directly — all interaction goes through the REST contract.
- **Validation before commit.** No transaction reaches the database until the engine approves it. A rejected transaction is returned to the client with a reason and is never persisted.
- **Push over poll.** Clients receive state changes through SignalR events emitted after each committed transaction, keeping the UI consistent without repeated balance requests.

## Tech Stack

`C#` · `ASP.NET Core 8` · `C++17` · `CMake` · `PostgreSQL` · `SignalR` · `REST` · `GitHub Codespaces`

## License

MIT