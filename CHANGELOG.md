# Changelog

## 0.1.1

- README images now load on nuget.org (absolute raw.githubusercontent.com
  URLs instead of repository-relative paths, which nuget.org does not show).

## 0.1.0

- `Hibira`: HTTP API client (`TriggerAsync`, `TriggerBatchAsync`,
  `SendToUserAsync`, channels and users, `TerminateUserConnectionsAsync`),
  approvals (`RequestApprovalAsync`, `GetApprovalAsync`), private, presence
  and user sign-in signatures, and webhook verification (including the
  previous secret during rotation).
- `Hibira.AspNetCore`: `HibiraHub` with the SignalR hub API: `Clients.All`,
  `Caller`, `Others`, `Group`, `OthersInGroup`, `User`, `Users`, `Client`,
  `Groups.AddToGroupAsync`, `OnConnectedAsync` / `OnDisconnectedAsync`, typed
  hubs (`HibiraHub<T>`), `IHubContext<THub>`, `[Authorize]` on hubs and
  methods, `HubException`, `[HubMethodName]`. `MapHibiraHub<T>(path)`,
  `MapHibiraWebhooks(path)`, in-memory or IDistributedCache connection store.
- `hibira-hub.js`: browser client with the `@microsoft/signalr` API, served
  as a static web asset.
- `Hibira.Client`: .NET client (Blazor WebAssembly, Blazor Server, MAUI,
  console) with the SignalR client API and automatic reconnect.
- Blazor sample and end-to-end checks against a real server.
