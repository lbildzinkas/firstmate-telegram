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

Also derived by hand, because the checkout at `e9a6675` was not at hand when the status answer was built. Each follows the field tables the spec carries for these commands (docs/spec.md sections 7.2.5 and 7.2.7); when FirstMate's real outputs are captured, replace these and keep the names:

| File | Shape |
| --- | --- |
| `bearings.json` | `fm-bearings.v1` with the spec's `/status` example: one decision, one PR to review, one landed item, one task under way, one gate |
| `bearings-many.json` | `fm-bearings.v1` with finished scout research, no decisions, 10 landed items and 10 gates, to exercise the eight-item cap and "+N more" |
| `fleet-snapshot.json` | `fm-fleet-snapshot.v1` with one captain-actionable hold, one done record, one task under way, one second-mate task, one queued record blocked by another |

The quota-axi fixtures live in `../quota-axi/`, all hand-derived from the spec's quota section: `quota-available.json` (42% left on the binding account scope), `quota-exhausted.json` and `quota-exhausted-no-reset.json` (runway `exhausted_now`), `quota-unknown-semantics.json`, `quota-keychain.json` (`keychain_prompt_required`), `quota-newer-schema.json` (`schemaVersion` 7) and `quota-missing-provider.json`.