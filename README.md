# firstmate-telegram

Talk to your [FirstMate](https://github.com/kunchenguid/firstmate) from a private Telegram chat.

firstmate-telegram is a small .NET companion that runs on the same Mac as FirstMate. It gives you:

- **Requests and replies**: send FirstMate a request from your phone and get its reply in the same chat.
- **Alerts**: a message when something needs you, such as a PR ready for review, a decision to make, or a real failure or blocker.
- **Status answers**: an instant `/status` built from FirstMate's records, with no FirstMate turn spent.
- **Availability pings**: `/ping` tells you whether FirstMate is running, listening and has model quota left.

It talks to FirstMate only through FirstMate's inbox and records, needs no FirstMate changes, and works whichever agent tool runs FirstMate. It pulls from Telegram, so it needs no server and no open port, and it works only while the Mac is awake.

## Status

**In progress.** The chat loop works: requests and replies, pairing, and the login agent. `/status` and `/ping` (with `/ping live`) work too.
Alerts, `/mute`, `/unmute`, `/back`, `/stop` from the phone, `/help` and the deny list come next; until then those commands answer "not available yet".

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

| Command | What it does |
| --- | --- |
| `firstmate-telegram setup` | Save the bot token, check the FirstMate home, pair your account |
| `firstmate-telegram stop` | Stop the bridge; it stays stopped until `start` |
| `firstmate-telegram start` | Start it again |
| `firstmate-telegram doctor` | Check everything and say how to fix what is wrong |
| `firstmate-telegram run` | Run the bridge in the terminal (the login agent runs this) |

## License

[MIT](LICENSE)
