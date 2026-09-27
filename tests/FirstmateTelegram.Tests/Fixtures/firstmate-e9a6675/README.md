# FirstMate fixtures (commit e9a6675)

Output of FirstMate's `bin/fm-inbox.sh` at commit `e9a6675ed188f3d77639cfe753451e07d68ab6a6`, the commit docs/spec.md was checked against.
The home path is replaced with `/Users/me/firstmate`.

Captured by running that script in a throwaway FirstMate home:

| File | Command |
| --- | --- |
| `note-created.json` | `note --request-id tg:123456789:42:7 --json -` (exit 0) |
| `note-replay.json` | the same request id again (exit 0, `outcome: replay`) |
| `note-saved-not-announced.json` | `note` from a copy of the script without `fm-wake-lib.sh`, so the wake fails (exit 3) |
| `announce-created.json` | `announce --json <id>` for that note (exit 0) |
| `announce-acknowledged.json` | `announce --json <id>` for a note already acknowledged (exit 0) |
| `receipts.json` | `receipts` after one reply and one acknowledgement |
| `receipts-after-cursor.json` | `receipts --after 000000000001` |
| `ready-not-running.json` | `ready` with no FirstMate session |
| `ready-not-running-away.json` | `ready` with no session and the away-mode record present |
| `return-unknown-subcommand.stderr` | `return --request-id x --json` (exit 1): today's FirstMate has no explicit-return hook |

Derived by hand, because a throwaway home cannot hold a live session lock. Each follows the `fm-primary-ready.v1` shape above and the values `cmd_ready` in that script can print:

| File | State |
| --- | --- |
| `ready-running-listening.json` | lock held, wake consumer healthy, present |
| `ready-running-away.json` | lock held, wake consumer healthy, away |
| `ready-not-picking-up.json` | lock held, wake consumer down |
| `ready-listening-unconfirmed.json` | lock held, wake consumer unknown |
