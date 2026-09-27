# Project agent memory

firstmate-telegram is a .NET 10 bridge between a private Telegram chat and a running FirstMate.
[docs/spec.md](docs/spec.md) is the design and [CONTEXT.md](CONTEXT.md) the glossary: use its words (bridge, request, reply, alert), and never call the bridge a relay.

## Commands

| What | Command |
| --- | --- |
| Check (run before every commit: build, format check, tests, shellcheck when installed) | `./check.sh` |
| Build | `dotnet build firstmate-telegram.slnx` |
| Tests | `dotnet test firstmate-telegram.slnx` |
| Contract tests against FirstMate's real scripts, in a throwaway home | `FIRSTMATE_CONTRACT_ROOT=<FirstMate checkout> dotnet test firstmate-telegram.slnx --filter ContractTests` |
| Fix formatting | `dotnet format firstmate-telegram.slnx` |
| Run the bridge in a terminal | `dotnet run --project src/FirstmateTelegram -- run` |
| Setup (token, FirstMate home, pairing) | `dotnet run --project src/FirstmateTelegram -- setup` |
| Install for the current user | `./install.sh` (`--dry-run` prints every change instead; `--uninstall [--purge]`) |

`run`, `setup` and `install.sh` use the real `~/.config`, `~/.local/state` and login agent of whoever runs them.

## Layout

- `src/FirstmateTelegram/`: `Bridge/` is the request/reply loop and the alert watchers (`AlertWatcher` for FirstMate's records, `AvailabilityMonitor` for away-mode availability), `Telegram/` the Bot API gateway with the deny-list `Redactor` applied to all outbound text, `FirstMate/` the clients for FirstMate's scripts (`fm-inbox.sh`, the snapshots), `quota-axi` and the fleet ledger, `State/` the bridge's own files, `Service/` the launchd login agent, `Cli/` the commands.
- `tests/FirstmateTelegram.Tests/`: `Support/BridgeHarness.cs` wires a bridge to `Fakes/` (an in-process Bot API server and stub FirstMate scripts in a throwaway FirstMate home). `Fixtures/firstmate-e9a6675/README.md` says how the FirstMate outputs were captured.

## Sharp edges

- Tests never call the real Telegram API or a real FirstMate home, and never install the login agent; `install.sh` runs in tests only with `--dry-run` and a throwaway `HOME`.
- Never commit a token-shaped literal: fake bot tokens are assembled at run time by `tests/FirstmateTelegram.Tests/Support/FakeTokens.cs` so secret scanning cannot flag them.
- Log lines carry times and ids only, never message text, and every line passes `TokenMask`.
- Telegram's position advances only over handled updates, and FirstMate's reply cursor only after a reply's last part is sent. `CrashWindowTests` guard both; keep them passing.
- An alert's dedupe key is written to `alerts.json` before the message is sent and the Telegram message id after; `alerts.json` is the only file holding alert or chat message text, kept for replies to alerts. A settle watch keeps its raw worker status line in `state.json` until its window closes.
- Inside `FirstmateTelegram.*` namespaces, `Telegram.` resolves to `FirstmateTelegram.Telegram`, so import Telegram.Bot with `using Telegram.Bot;` at the top of the file.

## Maintaining this file

Keep this file for knowledge useful to almost every future agent session in this project.
Do not repeat what the codebase already shows; point to the authoritative file or command instead.
Prefer rewriting or pruning existing entries over appending new ones.
When updating this file, preserve this bar for all agents and keep entries concise.
