# FirstMate fixtures (commit e9a6675)

Output of FirstMate's scripts at commit `e9a6675ed188f3d77639cfe753451e07d68ab6a6`, the commit docs/spec.md was checked against.
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
| `ready-running-quiet.json` | lock held, wake consumer healthy, quiet |
| `ready-not-picking-up.json` | lock held, wake consumer down |
| `ready-listening-unconfirmed.json` | lock held, wake consumer unknown |

Captured from the same commit's real `bin/fm-bearings-snapshot.sh` and `bin/fm-fleet-snapshot.sh`, in a throwaway home seeded with one held decision, three tasks (a finished ship task with a recorded PR, a finished scout with a report, one working), two queued rows and one landed item. `FM_BEARINGS_NOW`/`FM_SNAPSHOT_NOW` pin the generation time, fakebin `tmux`/`no-mistakes` stubs stand in for the local tools (no network, no real tmux server, exactly how FirstMate's own tests fake them), and the home path is rewritten to `/Users/me/firstmate`:

| File | Command |
| --- | --- |
| `bearings.json` | `fm-bearings-snapshot.sh --json` on that seeded home |
| `bearings-many.json` | the same with ten landed items and ten gates, captured with `FM_BEARINGS_LANDED=10` so the document outruns the bridge's eight-item cap |
| `fleet-snapshot.json` | `fm-fleet-snapshot.sh --json` on the same seeded home as `bearings.json` |

The contract tests (`ContractTests.cs`) run all of `fm-inbox.sh`, `fm-bearings-snapshot.sh` and `fm-fleet-snapshot.sh` against the real scripts in a throwaway home when `FIRSTMATE_CONTRACT_ROOT` names a checkout.

The fleet ledger has no captured fixture, because it is a file FirstMate's own events write, not a script's output: the ledger records the tests append (`Fakes/FakeFirstMateHome.AppendLedger`) follow the `v: 1` record contract in FirstMate's docs/fleet-ledger.md as summarized in spec 7.2.6, and `return-unknown-subcommand.stderr` above covers today's FirstMate refusing the return subcommand.

The quota-axi fixtures live in `../quota-axi/`, all hand-derived from the spec's quota section: `quota-available.json` (42% left on the binding account scope), `quota-exhausted.json` and `quota-exhausted-no-reset.json` (runway `exhausted_now`), `quota-unknown-semantics.json`, `quota-keychain.json` (`keychain_prompt_required`), `quota-newer-schema.json` (`schemaVersion` 7) and `quota-missing-provider.json`.