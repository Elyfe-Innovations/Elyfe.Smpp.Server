# Elyfe.Smpp.Server

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

An open-source **SMSC-side SMPP server** for .NET. It lets your application act as an
SMSC: accepting connections from ESMEs (External Short Messaging Entities) over SMPP
**v3.4** and **v5.0**, authenticating them, and bridging their traffic into your own
message-handling pipeline.

It is designed to be embedded into any .NET host (generic host, ASP.NET Core, or
[.NET Aspire](https://learn.microsoft.com/dotnet/aspire/)) via dependency injection.

> Built on top of the PDU codec from the JamaaTech SMPP library
> (`JamaaTech.Smpp.Net.Lib`).

## Features

- **SMSC role** — accepts `bind_transmitter`, `bind_receiver` and `bind_transceiver`.
- **SMPP v3.4 and v5.0** with bind-time interface-version negotiation.
- **Pluggable authentication** — config-based, file-based, or your own `IAuthenticator`.
- **Message bridge** — implement `IMessageHandler` to route `submit_sm` into your system
  and push `deliver_sm` (MO / delivery receipts) back to bound sessions.
- **Session management** — global and per-`system_id` connection caps, idle-timeout
  sweeping, `enquire_link` keepalive, and graceful shutdown (`unbind` drain).
- **Per-session throttling** — token-bucket submit rate limiting.
- **TLS** — optional SMPP-over-TLS with a server certificate.
- **Observability** — OpenTelemetry metrics (`Elyfe.Smpp.Server` meter) and an
  `IHealthCheck` for dashboards.

## Supported PDUs

| PDU | Direction | Status |
| --- | --- | --- |
| `bind_transmitter` / `_receiver` / `_transceiver` (+resp) | ESME → SMSC | ✅ |
| `unbind` (+resp) | both | ✅ |
| `enquire_link` (+resp) | both | ✅ |
| `submit_sm` (+resp) | ESME → SMSC | ✅ |
| `deliver_sm` (+resp) | SMSC → ESME | ✅ |
| `data_sm` (+resp) | both | ✅ |
| `query_sm` (+resp) | ESME → SMSC | ✅ |
| `cancel_sm` (+resp) | ESME → SMSC | ✅ |
| `replace_sm` (+resp) | ESME → SMSC | ✅ |
| `generic_nack` | both | ✅ |
| `submit_multi`, `broadcast_sm` (v5.0) | ESME → SMSC | ⛔ `generic_nack` (not in codec) |

## Quick start

### 1. Install

Add a project/package reference to `Elyfe.Smpp.Server`.

### 2. Implement a message handler

```csharp
using Elyfe.Smpp.Server.Bridge;

public sealed class MyMessageHandler : IMessageHandler
{
    public Task<SmppSubmitResult> HandleSubmitAsync(
        SmppSubmitRequest request, CancellationToken ct)
    {
        // request.SystemId, request.SourceAddress, request.DestinationAddress, request.Message
        // ... route into your own pipeline ...
        return Task.FromResult(SmppSubmitResult.Success(Guid.NewGuid().ToString("N")));
    }

    // QueryMessageStateAsync / CancelMessageAsync / ReplaceMessageAsync ...
}
```

### 3. Register the server

```csharp
using Elyfe.Smpp.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IMessageHandler, MyMessageHandler>();
builder.Services.AddSmppConfigAuthentication(builder.Configuration);
builder.Services.AddSmppServer(builder.Configuration);

// optional: health check
builder.Services.AddHealthChecks().AddSmppServerHealthCheck();

await builder.Build().RunAsync();
```

The server runs as an `IHostedService`, so it starts and stops with the host.

## Configuration reference

Bind from the `SmppServer` configuration section:

```json
{
  "SmppServer": {
    "Port": 2775,
    "BindAddress": "0.0.0.0",
    "SystemId": "My-SMSC",
    "MaxSessions": 1000,
    "MaxSessionsPerSystemId": 10,
    "IdleTimeout": "00:05:00",
    "BindTimeout": "00:00:30",
    "EnquireLinkInterval": "00:01:00",
    "MinInterfaceVersion": 52,
    "MaxInterfaceVersion": 80,
    "MaxSubmitsPerSecond": 100,
    "WindowSize": 100,
    "Tls": {
      "Enabled": false,
      "CertificatePath": "/certs/smpp.pfx",
      "CertificatePassword": ""
    }
  }
}
```

| Option | Default | Description |
| --- | --- | --- |
| `Port` | `2775` | TCP listen port (IANA SMPP). |
| `BindAddress` | `0.0.0.0` | Interface to bind. |
| `SystemId` | `Elyfe.Smpp.Server` | SMSC system id sent in bind responses. |
| `MaxSessions` | `1000` | Global concurrent-session cap (0 = unlimited). |
| `MaxSessionsPerSystemId` | `10` | Per-`system_id` cap (0 = unlimited). |
| `IdleTimeout` | `00:05:00` | Idle session is closed after this. |
| `BindTimeout` | `00:00:30` | Time to wait for a bind after TCP accept. |
| `EnquireLinkInterval` | `00:01:00` | Expected keepalive interval. |
| `MinInterfaceVersion` | `0x34` (52) | Lowest accepted SMPP version. |
| `MaxInterfaceVersion` | `0x50` (80) | Highest accepted SMPP version. |
| `MaxSubmitsPerSecond` | `100` | Per-session submit rate limit (0 = off). |
| `WindowSize` | `100` | Max outstanding unacknowledged PDUs (0 = off). |
| `Tls.Enabled` | `false` | Require TLS before SMPP traffic. |
| `Tls.CertificatePath` | – | PKCS#12 (.pfx) server certificate. |
| `Tls.CertificatePassword` | – | Certificate password. |

## Authentication

Choose one of the built-in authenticators or supply your own `IAuthenticator`.

**Config-based** — accounts live in the `SmppAuthentication` section:

```csharp
builder.Services.AddSmppConfigAuthentication(builder.Configuration);
```

```json
{
  "SmppAuthentication": {
    "Accounts": [
      { "SystemId": "acme", "Password": "<sha256-hex-or-plaintext>", "TenantId": "tenant-1", "Enabled": true }
    ]
  }
}
```

Passwords may be plaintext or a lowercase hex SHA-256 hash.

**File-based** — accounts in an external JSON file:

```csharp
builder.Services.AddSmppFileAuthentication("/etc/smpp/accounts.json");
```

**Custom** — register any `IAuthenticator` implementation in DI.

## Observability

Metrics are emitted under the meter name `Elyfe.Smpp.Server`:

- `smpp.server.binds` (counter, tag `result`)
- `smpp.server.submits` (counter, tag `result`)
- `smpp.server.errors` (counter, tag `kind`)
- `smpp.server.submit.duration` (histogram, ms)
- `smpp.server.sessions.active` (observable gauge)

Register the meter in your OpenTelemetry pipeline:

```csharp
metrics.AddMeter(Elyfe.Smpp.Server.SmppServerMetrics.MeterName);
```

A health check (`SmppServerHealthCheck`) reports `Healthy` while the listener is
accepting connections:

```csharp
builder.Services.AddHealthChecks().AddSmppServerHealthCheck();
```

## Build & test

```bash
dotnet build Elyfe.Smpp.Server.slnx
dotnet test  Elyfe.Smpp.Server.slnx
```

## Contributing

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) and our
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
