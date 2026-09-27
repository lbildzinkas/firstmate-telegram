# firstmate-telegram

Talk to your [FirstMate](https://github.com/kunchenguid/firstmate) from a private Telegram chat.

firstmate-telegram is a small .NET companion that runs on the same Mac as FirstMate. It gives you:

- **Requests and replies**: send FirstMate a request from your phone and get its reply in the same chat.
- **Alerts**: a message when something needs you: a PR ready for review, finished research, a decision to make, a real blocker or failure, and, while you are in away mode, FirstMate stopping or running out of model quota.
- **Status answers**: an instant `/status` built from FirstMate's records, with no FirstMate turn spent.
- **Availability pings**: `/ping` tells you whether FirstMate is running, listening and has model quota left.

It talks to FirstMate only through FirstMate's inbox and records, needs no FirstMate changes, and works whichever agent tool runs FirstMate. It pulls from Telegram, so it needs no server and no open port, and it works only while the Mac is awake.

## Status

**v1 complete.** The chat loop (requests, replies, pairing), `/status`, `/ping` with `/ping live`, alerts with `/mute` and `/unmute`, `/back` (in its "needs a newer FirstMate" form until FirstMate gains the return hook), `/stop`, `/help` and the deny list (shipped empty) all work.

- [docs/spec.md](docs/spec.md) is the v1 specification.
- [CONTEXT.md](CONTEXT.md) is the glossary.

## Install

You need macOS, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), a FirstMate home, and a bot of your own from [BotFather](https://t.me/BotFather).

```sh
git clone https://github.com/lbildzinkas/firstmate-telegram
cd firstmate-telegram
./install.sh
```

`install.sh` builds the app, links `~/.local/bin/firstmate-telegram`, runs `firstmate-telegram setup` the first time, installs the login agent and runs `firstmate-telegram doctor`.
Setup saves the bot token, checks the FirstMate home, and pairs your Telegram account: you send the bot a message and confirm it on the Mac.
Run `./install.sh` again to upgrade, `./install.sh --uninstall` to remove it (add `--purge` to also remove its configuration and state), and add `--dry-run` to see what it would do.
An upgrade swaps the app folder and reloads the login agent. Reloading first waits out the short window in which macOS launchd is still removing the old agent, and retries loading if it collides with that removal anyway. If loading still fails, install.sh stops and says exactly what to run: `firstmate-telegram start` loads the agent, and `firstmate-telegram doctor` checks it (doctor reports the agent as `installed but not loaded` until then).

## The chat

From Telegram, anything that is not one of these commands is sent to FirstMate as a request, and FirstMate's reply arrives threaded to your message:

| Command | What it does |
| --- | --- |
| `/status` | FirstMate at a glance, from its saved records |
| `/ping`, `/ping live` | Availability check; `live` asks FirstMate itself |
| `/mute <duration>`, `/unmute` | Alerts silent for a while (`30m`, `2h`, `1d`; plain `/mute` means 1 h) and back on |
| `/back` | Return from away mode (needs a newer FirstMate until its return hook exists) |
| `/stop` | Stop the bridge from the phone; only `start` on the Mac restarts it |
| `/help` | List the commands |

Alerts come from FirstMate's own records: the fleet activity ledger (`state/fleet-ledger.jsonl`, which the FirstMate user turns on by creating `config/fleet-ledger` in the FirstMate home) for PRs ready for review, finished research and worker blockers or failures, and the fleet snapshot for decisions waiting on you. Availability alerts fire only in true away mode. Replying to an alert sends FirstMate a request tied to that alert. Projects named in the deny list (`deny_list` in the configuration, empty by default) appear in Telegram only as "a private project".

## Local commands

| Command | What it does |
| --- | --- |
| `firstmate-telegram setup` | Save the bot token, check the FirstMate home, pair your account |
| `firstmate-telegram stop` | Stop the bridge; it stays stopped until `start` |
| `firstmate-telegram start` | Start it again |
| `firstmate-telegram doctor` | Check everything and say how to fix what is wrong |
| `firstmate-telegram run` | Run the bridge in the terminal (the login agent runs this) |

## License

[MIT](LICENSE)
