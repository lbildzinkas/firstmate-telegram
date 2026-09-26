# firstmate-telegram

Talk to your [FirstMate](https://github.com/kunchenguid/firstmate) from a private Telegram chat.

firstmate-telegram is a small .NET companion that runs on the same Mac as FirstMate. It gives you:

- **Requests and replies**: send FirstMate a request from your phone and get its reply in the same chat.
- **Alerts**: a message when something needs you, such as a PR ready for review, a decision to make, or a real failure or blocker.
- **Status answers**: an instant `/status` built from FirstMate's records, with no FirstMate turn spent.
- **Availability pings**: `/ping` tells you whether FirstMate is running, listening and has model quota left.

It talks to FirstMate only through FirstMate's inbox and records, needs no FirstMate changes, and works whichever agent tool runs FirstMate. It pulls from Telegram, so it needs no server and no open port, and it works only while the Mac is awake.

## Status

**Specification stage.** There is no code yet.

- [docs/spec.md](docs/spec.md) is the v1 specification.
- [CONTEXT.md](CONTEXT.md) is the glossary.

## License

[MIT](LICENSE)
