# Setting up firstmate-telegram

This tutorial takes you from nothing to talking with your FirstMate from Telegram.
It takes about 15 minutes. You need to be at the Mac that runs FirstMate, with your phone at hand.

Throughout, `@your_firstmate_bot` stands for your bot's username and `~/firstmate` for your FirstMate home. Use your own.

## Contents

1. [What you get](#1-what-you-get)
2. [Prerequisites](#2-prerequisites)
3. [Create your bot in BotFather](#3-create-your-bot-in-botfather)
4. [Install, with a dry run first](#4-install-with-a-dry-run-first)
5. [Setup: token, FirstMate home, pairing](#5-setup-token-firstmate-home-pairing)
6. [The login agent](#6-the-login-agent)
7. [Check everything with doctor](#7-check-everything-with-doctor)
8. [Optional next steps](#8-optional-next-steps)
9. [Your first message](#9-your-first-message)
10. [Chat commands](#10-chat-commands)
11. [Local commands](#11-local-commands)
12. [What a Telegram request may do](#12-what-a-telegram-request-may-do)
13. [Keeping the token safe](#13-keeping-the-token-safe)
14. [Troubleshooting](#14-troubleshooting)
15. [Upgrading](#15-upgrading)
16. [Uninstalling](#16-uninstalling)

## 1. What you get

firstmate-telegram is a bridge between a private Telegram chat and the FirstMate running on your Mac.

- Anything you type to your bot becomes a **request** in FirstMate's inbox, and FirstMate's **reply** comes back threaded to your message.
- **Alerts** tell you when something needs you: a PR ready for review, finished research, a decision to make, a lasting blocker or failure.
- `/status` and `/ping` answer instantly from FirstMate's records, without spending a FirstMate turn.

The bridge pulls messages from Telegram, so it needs no server and no open port.
It runs only while your Mac is awake and you are logged in.

## 2. Prerequisites

You need all four of these.

### macOS

The bridge runs on macOS only, on Apple silicon or Intel. `install.sh` stops with `firstmate-telegram v1 runs on macOS only.` anywhere else.

### The .NET 10 SDK

Install it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0), or with Homebrew. Then check:

```sh
dotnet --list-sdks
```

You need at least one line starting with `10.`. Without it, `install.sh` stops and says the .NET 10 SDK is required.

### A recent enough FirstMate

You need a working FirstMate home. The **FirstMate home** is the folder that holds `bin/fm-inbox.sh`; it is the folder `FM_HOME` points to.

The bridge needs a FirstMate whose inbox supports **readiness** and **receipts**: FirstMate `main` at or after commit `e9a6675` (2026-09-26).
To tell, ask the inbox both questions yourself (they only read, they change nothing):

```sh
FM_HOME=~/firstmate ~/firstmate/bin/fm-inbox.sh ready | head -c 40; echo
FM_HOME=~/firstmate ~/firstmate/bin/fm-inbox.sh receipts | head -c 40; echo
```

Each prints the start of a line of JSON (`head` cuts the rest). Look at the `schema`:

- `ready` must print `{"schema":"fm-primary-ready.v1", ...}`
- `receipts` must print `{"schema":"fm-inbox-receipts.v1", ...}`

If either command says `unknown subcommand`, or prints no such schema, update FirstMate first.
Setup runs the same `ready` check and refuses a home that fails it.

### The tools FirstMate's scripts use

`bash`, `python3` and `jq` must be on your `PATH`; FirstMate's scripts need them, and `doctor` checks for them.
`quota-axi` is optional: with it, `/ping` also shows how much model quota is left.

## 3. Create your bot in BotFather

The bridge talks through a bot that belongs to you and nobody else.

1. In Telegram, open a chat with [@BotFather](https://t.me/BotFather).
2. Send `/newbot`.
3. Give it a display name, for example `My FirstMate`.
4. Give it a username ending in `bot`, for example `your_firstmate_bot`.
5. BotFather answers with a **token** shaped like `123456789:AAE...`. Leave that message where it is for now; you will copy the token straight into setup in [section 5](#5-setup-token-firstmate-home-pairing). Do not paste it anywhere else (see [section 13](#13-keeping-the-token-safe)).

Then turn off groups, so nobody can add your bot to a group:

1. Send `/mybots` to BotFather and pick your bot.
2. Choose **Bot Settings**, then **Allow Groups?**, then **Turn groups off**.

The bridge already ignores every message that is not from you in your private chat, so this is a second lock on the door.

## 4. Install, with a dry run first

Clone the project and look at what the installer would do before it does anything:

```sh
git clone https://github.com/lbildzinkas/firstmate-telegram
cd firstmate-telegram
./install.sh --dry-run
```

The dry run prints every change as `would run: ...` and changes nothing. On a first install it looks like this (paths shortened):

```text
would run: rm -rf ~/.local/share/firstmate-telegram/app.new ~/.local/share/firstmate-telegram/app.old
would run: dotnet publish .../src/FirstmateTelegram/FirstmateTelegram.csproj --configuration Release --runtime osx-arm64 --self-contained false --nologo --output ~/.local/share/firstmate-telegram/app.new
would run: mv ~/.local/share/firstmate-telegram/app.new ~/.local/share/firstmate-telegram/app
would run: rm -rf ~/.local/share/firstmate-telegram/app.old
would run: mkdir -p ~/.local/bin
would run: ln -sfn ~/.local/share/firstmate-telegram/app/firstmate-telegram ~/.local/bin/firstmate-telegram
would run: ~/.local/share/firstmate-telegram/app/firstmate-telegram setup
would run: ~/.local/share/firstmate-telegram/app/firstmate-telegram service install
would run: ~/.local/share/firstmate-telegram/app/firstmate-telegram doctor
```

In words, `install.sh`:

1. builds the app into `~/.local/share/firstmate-telegram/app/`;
2. links it as `~/.local/bin/firstmate-telegram`;
3. runs `firstmate-telegram setup`, but only when there is no configuration yet;
4. installs the login agent with `firstmate-telegram service install`;
5. runs `firstmate-telegram doctor`.

When you are happy with it, **run the real install yourself, in your own terminal**:

```sh
./install.sh
```

Setup is interactive and asks for your bot token, so run it yourself rather than through an AI assistant or any other chat. [Section 13](#13-keeping-the-token-safe) explains why.

The rest of this tutorial uses the `firstmate-telegram` command. If your shell says `command not found`, `~/.local/bin` is not on your `PATH`: add it (for zsh, `export PATH="$HOME/.local/bin:$PATH"` in `~/.zshrc`), or call `~/.local/bin/firstmate-telegram` directly.

## 5. Setup: token, FirstMate home, pairing

`install.sh` starts setup for you on the first install. You can also run it on its own at any time with `firstmate-telegram setup`, for example to change the FirstMate home or pair a different account.
Setup changes nothing in your FirstMate home.

### The token

```text
Bot token from BotFather:
```

Paste the token from BotFather and press Enter. **The input is hidden**: nothing appears while you paste, which is expected.

Setup checks the token with Telegram and saves it:

```text
Token accepted for @your_firstmate_bot. Saved it to ~/.config/firstmate-telegram/token.
```

- `That does not look like a bot token; BotFather gives one shaped like 123456789:AAE...` means the paste was incomplete. Paste again.
- `Telegram rejected this token. Copy it again from BotFather.` means the token is wrong or was revoked.
- When you run setup again later, it offers `(press Enter to keep the saved token)`.

If the bot has a webhook set (only if you used it with another tool before), setup explains that the bridge cannot pull messages while a webhook is set and asks to delete it. Answer `y`; messages Telegram is holding are kept.

### The FirstMate home

```text
FirstMate home folder [~/firstmate]:
```

Enter the full path of your FirstMate home, the folder holding `bin/fm-inbox.sh`. If `FM_HOME` is set in your shell, setup suggests it in brackets and Enter accepts it. `~/` works too.

Setup runs `fm-inbox.sh ready` there and reports what it found:

```text
FirstMate home checked: running, listening.
```

FirstMate does not have to be running; `not running` is fine here. What setup refuses:

- `.../bin/fm-inbox.sh is missing; that does not look like a FirstMate home.`: you gave the wrong folder. Give the one that contains `bin/`.
- `FirstMate's inbox did not answer as expected (...). FirstMate at or after the version this bridge needs is required.`: your FirstMate is too old. Update it (see [Prerequisites](#a-recent-enough-firstmate)).

### Pairing

Pairing tells the bridge which Telegram account is yours. It is confirmed **on the Mac**, never from the phone.

```text
Send any message to @your_firstmate_bot from your own Telegram account.
```

1. On your phone, open a chat with your bot (search for its username, then tap **Start**) and send it anything, for example `hello`.
2. Back on the Mac, setup shows who sent it:

   ```text
   Message from Telegram user id 123456789: Your Name (@your_username), sent 2026-09-27 10:15.
   Allow only this account? [y/N]
   ```

3. Check that it is you, then type `y`. Anything else keeps waiting for the next message.
4. The bot replies `Paired.` in the chat, and setup prints `Paired with user id 123456789.`

From now on the bridge accepts messages only from that account, only in that private chat. Everything else is ignored silently, with no reply, so strangers cannot even tell the bot is alive.

> **The pairing message is not delivered to FirstMate.** Setup confirms it to Telegram so that it never becomes a request. Your first real request is the next message you send, in [section 9](#9-your-first-message).

Setup then runs the same checks as `doctor`, prints some optional next steps (covered in [section 8](#8-optional-next-steps)) and saves the configuration:

```text
Saved ~/.config/firstmate-telegram/config.json.
```

On a first install you will also see `Start the bridge with ./install.sh (as a login agent), ...` and a `login agent: not installed` problem in the checks. Both are expected: `install.sh` installs the login agent next.

## 6. The login agent

The bridge runs in the background as a macOS **login agent**, `io.github.lbildzinkas.firstmate-telegram`, which `install.sh` installs and loads:

```text
Login agent installed: ~/Library/LaunchAgents/io.github.lbildzinkas.firstmate-telegram.plist
```

- It starts when you log in and runs while you are logged in. After a reboot it starts at login, not at the login window.
- It restarts the bridge if it crashes, but not after you stop it on purpose.
- It runs whether or not FirstMate does. That is what lets `/ping` tell you FirstMate is down.
- It remembers where `bash`, `python3`, `jq`, `quota-axi` and `dotnet` were in the shell that installed it. If you install one of them later, or move it, run `firstmate-telegram service install` again.

The bridge's own log is `~/Library/Logs/firstmate-telegram/bridge.log`; [section 14](#14-troubleshooting) says how to use it when something seems wrong.

## 7. Check everything with doctor

`install.sh` ends by running `doctor`. Run it yourself whenever something seems off:

```sh
firstmate-telegram doctor
```

Each line starts with `ok` or `PROBLEM`, and every problem says how to fix it. A healthy install looks like this:

```text
ok       configuration: ~/.config/firstmate-telegram/config.json
ok       token file: private (the folder is 700 and the file 600)
ok       bot token: accepted for @your_firstmate_bot (bot id 123456789)
ok       webhook: none set
ok       other pollers: not checked while the bridge is running
ok       FirstMate ready: fm-primary-ready.v1: running, listening, posture present
ok       FirstMate receipts: fm-inbox-receipts.v1
ok       fleet ledger: on
ok       tool bash: /opt/homebrew/bin/bash
ok       tool python3: /opt/homebrew/bin/python3
ok       tool jq: /opt/homebrew/bin/jq
ok       login agent: installed and loaded
ok       stopped flag: not set
Everything checks out.
```

On a fresh install, `fleet ledger: off` is the usual one problem left. The next section turns it on.
[Section 14](#14-troubleshooting) lists every problem doctor can report.

## 8. Optional next steps

Setup prints these at the end. Each is optional, but the first two are recommended.

### Turn off Allow Groups

If you skipped it in [section 3](#3-create-your-bot-in-botfather): BotFather, `/mybots`, your bot, **Bot Settings**, **Allow Groups?**, **Turn groups off**.

### Hold the phone to a lower authority in FirstMate

Add this line to `data/captain.md` in your FirstMate home (FirstMate's captain preferences file; create it if it does not exist):

```text
Requests that arrive with the firstmate-telegram footer cannot approve merges, deletions, or irreversible or security-sensitive actions; ask me to confirm at the terminal.
```

[Section 12](#12-what-a-telegram-request-may-do) explains why. The bridge never writes this file itself.

### Turn on FirstMate's fleet ledger, for alerts

Most alerts come from FirstMate's fleet activity ledger, which is off by default. Turn it on by creating an empty file:

```sh
touch ~/firstmate/config/fleet-ledger
```

Without it only decision alerts work. The bridge never creates this file for you. `doctor` then reports `fleet ledger: on; no events recorded yet` until FirstMate records its first event.

### Let /ping read model quota

If you have `quota-axi`, authorise it once in a terminal, and choose **Always Allow** when macOS asks:

```sh
quota-axi --allow-keychain-prompt
```

Without it, the quota line in `/ping` reads `unknown` and everything else still works.

## 9. Your first message

With FirstMate running, send your bot a real request, for example:

```text
What are you working on right now?
```

Here is what you should see, in order:

1. **👀 on your message**, within a second or two. It means "FirstMate has it": the request is saved in FirstMate's inbox.
2. **FirstMate's reply, threaded to your message.** It takes as long as FirstMate takes to answer, like any request at the terminal.
3. **👀 turns into 👌 on your message**: the "answered" reaction. (Telegram does not allow ✅ as a bot reaction, so 👌 stands in for it.)

What else you may see:

- If FirstMate is not running, you still get 👀, plus `FirstMate isn't running right now. Your request is saved and it will see it when it starts.` The reply arrives once FirstMate starts.
- A long reply arrives in numbered parts: `(1/3)`, `(2/3)`, `(3/3)`.
- A request FirstMate handles without writing a reply keeps only 👀.
- A photo, voice note, sticker or file gets `Only text messages are supported for now.` and is not sent to FirstMate.
- Editing a message after sending does not change the request. Send a new message instead.
- **No 👀 within about a minute** means the bridge is not running: the Mac is asleep, offline or logged out, or the bridge is stopped. Telegram holds your messages for 24 hours and the bridge catches up when the Mac wakes; messages older than that are lost. See [section 14](#14-troubleshooting).

Try `/ping` too. It answers in a few seconds whether or not FirstMate is running.

## 10. Chat commands

In the chat, Telegram's command menu shows these. Anything else, including an unknown `/word`, is sent to FirstMate as a request.

| Command | What it does |
| --- | --- |
| `/status` | FirstMate at a glance, from its saved records: **Needs you**, **Recently landed**, **Under way**, **Next**. Instant, and spends no model quota. |
| `/ping` | Instant availability check. The first line is a verdict such as `Ready`, `Not running; requests will queue` or `Running, but Claude quota is out until 21:40`; the lines below show the checks behind it. |
| `/ping live` | Asks FirstMate itself to answer within 60 s: `Live: FirstMate answered in 14 s.` or `Not available: FirstMate did not answer within 60 s.` This is a real FirstMate turn and uses a little quota. |
| `/mute <duration>` | Alerts arrive silently (no sound or vibration) for a while: `30m`, `2h`, `1d`, or combinations like `1h30m`, up to `7d`. Plain `/mute` means one hour. Nothing is dropped, and replies to your requests are never muted. |
| `/unmute` | Alerts make sound again. |
| `/back` | Return from away mode. With today's FirstMate, while away mode is on, it answers that ending away mode from Telegram needs a newer FirstMate, so end it at the terminal for now; `/back` starts working on its own once FirstMate gains the return hook. Plain words such as "I'm back" never end away mode. |
| `/stop` | Stops the bridge from the phone. It stays stopped, across restarts and reboots, until you run `firstmate-telegram start` on the Mac. Someone holding your phone can stop it but not start it again. |
| `/help` | Lists these commands. |

### Alerts

The bridge sends an alert when something needs you:

| Alert | Example |
| --- | --- |
| PR ready for review | `Ready for your review: fix login redirect https://github.com/acme/webapp/pull/7` |
| Research finished | `Research finished: search provider options. Reply to this message to get the findings.` |
| Decision to make | `Decision needed: billing API versioning. Options: A path prefix, B header. Reply to this message with your answer.` |
| Waiting on a decision, blocked, failed | `Blocked: add CSV export. <status text>`, sent only if still open after 15 minutes, because FirstMate often resolves these without you |
| Availability, only in away mode | `FirstMate stopped while you are away. Requests will queue until it starts.` |

- **Reply to an alert** with Telegram's reply to send FirstMate a request tied to it. That is how you answer a decision from the phone: reply "go with option B".
- Availability alerts fire only while FirstMate is truly in away mode, not in quiet mode. Otherwise use `/ping`.
- Each event alerts once. On its first start the bridge does not alert on anything that happened before.

## 11. Local commands

On the Mac:

| Command | What it does |
| --- | --- |
| `firstmate-telegram setup` | Save the bot token, check the FirstMate home, and pair your Telegram account. Run it again to change any of these. |
| `firstmate-telegram doctor` | Check everything and say how to fix what is wrong. |
| `firstmate-telegram stop` | Stop the bridge. It stays stopped, across logins and restarts, until `start`. |
| `firstmate-telegram start` | Clear the stopped flag and start the login agent. |
| `firstmate-telegram run` | Run the bridge in this terminal instead of as a login agent (the login agent runs this). Stop the login agent first; only one copy runs at a time. |
| `firstmate-telegram service install` | Write and load the login agent (`install.sh` runs this). |
| `firstmate-telegram service uninstall` | Unload and remove the login agent. |
| `firstmate-telegram version` | Print the version. |

Where things live:

| Path | What |
| --- | --- |
| `~/.config/firstmate-telegram/config.json` | Configuration (FirstMate home, your user id, options) |
| `~/.config/firstmate-telegram/token` | The bot token, readable by you only |
| `~/.local/state/firstmate-telegram/` | The bridge's own state |
| `~/Library/Logs/firstmate-telegram/` | Logs: times and ids only, never message text or the token |

`config.json` also holds a few options, each with a default: `deny_list` (projects never named on Telegram, empty by default), `replied_reaction`, `live_ping_timeout_seconds`, `unresponsive_after_minutes`, `alert_settle_minutes` and `quota_provider`. [docs/spec.md, section 10.1](spec.md#101-configuration) describes each one. Changes take effect when the bridge restarts: `firstmate-telegram stop`, then `firstmate-telegram start`.

## 12. What a Telegram request may do

A request from Telegram **can** ask questions, start work and steer work in progress.
It **cannot** approve merges, deletions, or any irreversible or security-sensitive action. Those still need you at the terminal.
Alerts follow the same rule: a PR alert tells you the PR is ready for review, and merging it still happens at the terminal.

How this is enforced:

- The bridge adds a footer to every request, saying that it came from Telegram and stating this limit.
- FirstMate's own rules add a floor: away mode never widens what may be approved, and destructive or security-sensitive actions are never approved in advance.
- The line from [section 8](#hold-the-phone-to-a-lower-authority-in-firstmate) in `data/captain.md` makes it a standing preference FirstMate applies to every such request. **Add it.**

This is enforcement by instruction, not a hard lock: FirstMate cannot yet record where an inbox note came from. Treat your Telegram account as a key to your FirstMate: keep two-step verification on in Telegram, and if you lose your phone, follow the steps in the next section.

Telegram bot chats are not end-to-end encrypted, so Telegram's servers can read requests, replies and alerts. If a project must never be named there, add it to `deny_list` in `config.json`; it then appears only as "a private project".

## 13. Keeping the token safe

The bot token is the key to your bot. Anyone who has it can read what is sent to the bot and send messages as it.

- **Run setup yourself, in a terminal.** Paste the token only at setup's hidden `Bot token from BotFather:` prompt. Never paste it into a chat, an issue, an email, or an AI assistant, including one helping you install: the token would then sit in that conversation's history. If an assistant is helping, let it do the rest and run `firstmate-telegram setup` (or the first `./install.sh`) yourself.
- The bridge keeps the token in `~/.config/firstmate-telegram/token`, readable by you only, and never writes it to logs, command lines, the environment or the login agent file. It refuses to start if the file or its folder is readable by others.

### If the token leaks, or you lose your phone

1. In BotFather, send `/revoke`, pick your bot, and confirm. The old token stops working at once, and BotFather gives you a new one.
2. On the Mac, run `firstmate-telegram stop` if you want nothing running while you sort things out.
3. Run `firstmate-telegram setup` and paste the new token. Pair again if the account changed.
4. Run `firstmate-telegram start` if you stopped it in step 2, then `firstmate-telegram doctor`.
5. For a lost phone, also end its Telegram session from another device (Telegram **Settings**, **Devices**).

Until setup saves a new token, the bridge logs "token rejected" and checks again every 15 minutes, and `doctor` reports `Telegram rejected the token (revoked?)`.

## 14. Troubleshooting

Start with `firstmate-telegram doctor`. Find its `PROBLEM` line below.

| doctor says | What to do |
| --- | --- |
| `configuration: No configuration at ... Run firstmate-telegram setup first.` | Run `firstmate-telegram setup`. |
| `configuration: The configuration at ... is invalid: ...` | Fix the key named in the message in `config.json` (for example `"replied_reaction" must be one of Telegram's bot reaction emoji`), or run setup again, which writes a new configuration. |
| `token file: No bot token at ...` or `... is empty` | Run `firstmate-telegram setup`. |
| `token file: ... is readable by other users. Fix it with: chmod ...` | Run the `chmod` command it prints. |
| `bot token: Telegram rejected the token (revoked?)` | Get a new token from BotFather and run setup ([section 13](#if-the-token-leaks-or-you-lose-your-phone)). |
| `bot token: could not reach Telegram to check it` | The Mac is offline, or something blocks `api.telegram.org`. Check the network. |
| `webhook: a webhook is set, so long polling cannot work.` | Run `firstmate-telegram setup`, which offers to remove it. |
| `other pollers: another program is polling with this bot token, or a webhook is set.` | Only one program may use a token. Stop the other copy, such as a `firstmate-telegram run` in another terminal, or another tool using the same bot. |
| `FirstMate home: .../bin/fm-inbox.sh is missing.` | `firstmate_home` in `config.json` points to the wrong folder. Run setup and give the folder holding `bin/fm-inbox.sh`. |
| `FirstMate ready: ...` or `FirstMate receipts: ...` with an error | Your FirstMate is older than the bridge needs, or its scripts failed. Check [the prerequisite](#a-recent-enough-firstmate); the text after the colon is FirstMate's own error. |
| `... FirstMate's records use a newer format; update firstmate-telegram` | FirstMate is newer than this bridge. [Upgrade](#15-upgrading) firstmate-telegram. |
| `fleet ledger: off.` | Only decision alerts work. `touch ~/firstmate/config/fleet-ledger` ([section 8](#turn-on-firstmates-fleet-ledger-for-alerts)). |
| `tool jq: not found on PATH` (or `bash`, `python3`) | Install it, then run `firstmate-telegram service install` so the login agent's `PATH` includes it. |
| `login agent: not installed.` | Run `./install.sh` from your firstmate-telegram clone. |
| `login agent: installed but not loaded.` | Run `firstmate-telegram start`, then `firstmate-telegram doctor` again. |
| `stopped flag: set` | Someone ran `/stop` or `firstmate-telegram stop`. Run `firstmate-telegram start`. |

### The login agent does not load after an upgrade

An upgrade reloads the login agent. macOS can take a moment to remove the old one, and `install.sh` waits for that and retries. If loading still fails, `install.sh` stops with a message such as:

```text
launchctl bootstrap could not load the login agent (...): .... The login agent is installed but not loaded; run `firstmate-telegram start` to load it, then `firstmate-telegram doctor` to check it.
```

Do exactly that:

```sh
firstmate-telegram start
firstmate-telegram doctor
```

`doctor` should now report `login agent: installed and loaded`. Until then it reports `installed but not loaded`.

### Doctor says everything is fine, but messages are slow or never answered

`doctor` checks the installation, not the running bridge, so it can print `Everything checks out.` while the login agent restarts a crashing bridge every few seconds.
Ask launchd whether that is happening:

```sh
launchctl print gui/$(id -u)/io.github.lbildzinkas.firstmate-telegram
```

Look at `runs` and `last exit code` in the output: a `runs` count that keeps climbing, with a non-zero `last exit code`, means the login agent keeps restarting a bridge that crashes.
Then read the bridge's own log, `~/Library/Logs/firstmate-telegram/bridge.log`, for the error it dies on.

If the bridge is running steadily and messages are only slow, the wait can be on FirstMate's side; see [Known issues](../README.md#known-issues) in the README.

### Other symptoms

| Symptom | What to do |
| --- | --- |
| No 👀 on your message within a minute | The bridge is not running or cannot reach Telegram. Check that the Mac is awake, online and logged in, then run `doctor`. |
| 👀 but no reply for a long time | FirstMate has the request but has not answered. `/ping` says whether it is running, listening and has quota; `/ping live` asks it directly. |
| `FirstMate isn't running right now. Your request is saved ...` | Expected when FirstMate is stopped. Start FirstMate; it will see the request. |
| The bot does not react at all to a friend's messages | Expected. The bridge answers only your paired account. |
| Setup says `Another program is polling with this bot token.` | Stop the other program using the token, then run setup again. |
| Setup says `Another copy of the bridge is running` | Stop the `firstmate-telegram run` in another terminal, then run setup again. |
| `firstmate-telegram: command not found` | Add `~/.local/bin` to your `PATH` ([section 4](#4-install-with-a-dry-run-first)). |
| `/back` says away mode is still on | Expected with today's FirstMate: end away mode at the terminal. |

The log is `~/Library/Logs/firstmate-telegram/bridge.log`. It holds times, ids and errors, never message text or the token, so it is safe to read and share when asking for help. Crashes of the login agent itself land in `launchd.err.log` in the same folder.

## 15. Upgrading

```sh
cd firstmate-telegram
git pull
./install.sh --dry-run   # optional: see what it will do
./install.sh
```

`install.sh` rebuilds the app, swaps it in, and reloads the login agent. Your configuration, token, pairing and state are kept, and setup does not run again. It ends with `doctor`; if the login agent did not load, see [above](#the-login-agent-does-not-load-after-an-upgrade).

## 16. Uninstalling

```sh
cd firstmate-telegram
./install.sh --uninstall --dry-run   # optional: see what it will remove
./install.sh --uninstall
```

This removes the login agent, the app and the `~/.local/bin/firstmate-telegram` link, and keeps your configuration and state, so reinstalling later needs no setup.

To remove everything, including the configuration, token, state and logs:

```sh
./install.sh --uninstall --purge
```

Uninstalling changes nothing in your FirstMate home. If you added the `data/captain.md` line or turned on the fleet ledger, remove them yourself if you no longer want them. To retire the bot too, send `/deletebot` to BotFather.
