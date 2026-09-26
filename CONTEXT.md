# Telegram bridge

A private Telegram chat between a FirstMate user and their own bot, connected to a running FirstMate, so the user can reach FirstMate from a phone and FirstMate can reach the user.

## Language

**User**:
The one person who owns the FirstMate setup and the Telegram account the bridge answers.
_Avoid_: Captain (FirstMate's own word for the same person; use it only when quoting FirstMate), operator, owner

**Bridge**:
The add-on that connects the user's private Telegram chat to a running FirstMate.
_Avoid_: Relay (already FirstMate's name for its hosted Discord and X integration), gateway, bot (the bot is only the Telegram-side identity)

**Request**:
A message the user sends through the bridge for FirstMate to act on or answer.
_Avoid_: Command, prompt, note (a note is FirstMate's inbox record that carries a request)

**Reply**:
FirstMate's answer to one specific request.
_Avoid_: Response, follow-up

**Alert**:
A message FirstMate starts on its own to reach the user, carrying something that needs them.
_Avoid_: Notification, push, ping

**Status answer**:
An instant answer to "what is happening?" built from FirstMate's saved records without interrupting FirstMate.
_Avoid_: Summary, report

**Availability**:
Whether FirstMate can take and act on a request right now: running, listening, and able to use its model.
_Avoid_: Health, heartbeat (a FirstMate-internal term), uptime

**Ping**:
The user's on-demand availability check through the bridge.
_Avoid_: Health check, heartbeat

**Away mode**:
FirstMate's declared away posture, entered when the user says they are going away from the keyboard.
_Avoid_: afk (informal), quiet mode (a different posture where the user is still present)

**Private project**:
A project on the bridge's deny list, named only as "a private project" in anything sent to Telegram.
_Avoid_: Hidden project, redacted project

**Explicit return**:
The `/back` command sent through the bridge: the only way a Telegram request may end away mode.
Plain words in a request never end it.
_Avoid_: Back, return (unqualified)
