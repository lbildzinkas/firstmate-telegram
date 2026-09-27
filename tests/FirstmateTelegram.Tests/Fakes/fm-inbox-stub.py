#!/usr/bin/env python3
"""A stand-in for FirstMate's bin/fm-inbox.sh, installed in a throwaway FirstMate home for tests.

It keeps a real notes folder with request-id reservations, so a repeated request id replays the original note
as FirstMate does, and prints the JSON shapes captured from FirstMate e9a6675 (see Fixtures/). Every call is
recorded in fake/calls.jsonl with its standard input. A test scripts faults per subcommand in
fake/faults/<subcommand>.json: a JSON list, one entry used per call:

  {"exit": 1, "stderr": "..."}      fail before saving anything
  {"sleep": 5}                      hang before saving anything
  {"sleep_after_save": 5}           save the note, then hang (the caller times out)
  {"no_wake": true}                 save the note but fail to wake FirstMate (exit 3)
  {"stdout": "...", "exit": 0}      print this instead of the real answer
"""
import datetime
import json
import os
import secrets
import sys
import time

HOME = os.environ["FM_HOME"]
INBOX = os.path.join(HOME, "state", "inbox")
HANDLED = os.path.join(INBOX, "handled")
REQUESTS = os.path.join(INBOX, ".requests")
ANNOUNCED = os.path.join(INBOX, ".announced")
REPLIES = os.path.join(INBOX, ".replies")
FAKE = os.path.join(HOME, "fake")
BOUND = 20


def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def die(message, code=1):
    sys.stderr.write("fm-inbox: %s\n" % message)
    sys.exit(code)


def emit(document):
    json.dump(document, sys.stdout, separators=(",", ":"))
    sys.stdout.write("\n")
    sys.stdout.flush()


def record(argv, stdin):
    os.makedirs(FAKE, exist_ok=True)
    with open(os.path.join(FAKE, "calls.jsonl"), "a") as calls:
        calls.write(json.dumps({"argv": argv, "stdin": stdin, "fm_home": HOME}) + "\n")


def next_fault(subcommand):
    path = os.path.join(FAKE, "faults", subcommand + ".json")
    if not os.path.exists(path):
        return {}
    with open(path) as source:
        queue = json.load(source)
    if not queue:
        return {}
    fault = queue.pop(0)
    with open(path, "w") as target:
        json.dump(queue, target)
    return fault


def apply_early_fault(fault):
    if "sleep" in fault:
        time.sleep(fault["sleep"])
    if "stdout" in fault:
        sys.stdout.write(fault["stdout"])
        sys.exit(fault.get("exit", 0))
    if "exit" in fault:
        die(fault.get("stderr", "scripted failure"), fault["exit"])


def note_path(note_id):
    for folder in (INBOX, HANDLED):
        path = os.path.join(folder, note_id + ".note")
        if os.path.exists(path):
            return path
    return None


def write_note(note_id, request_id, body):
    os.makedirs(INBOX, exist_ok=True)
    with open(os.path.join(INBOX, note_id + ".note"), "w") as note:
        note.write("id=%s\nat=%s\nsource=text\nannounce_marker=1\n" % (note_id, now()))
        if request_id:
            note.write("request_id=%s\n" % request_id)
        note.write("--\n" + body + ("" if body.endswith("\n") else "\n"))


def announce(note_id, fault):
    """True when woken now or before, False when the wake failed, None when already acknowledged."""
    if os.path.exists(os.path.join(ANNOUNCED, note_id)):
        return True
    if not os.path.exists(os.path.join(INBOX, note_id + ".note")):
        return None
    if fault.get("no_wake"):
        return False
    os.makedirs(ANNOUNCED, exist_ok=True)
    with open(os.path.join(ANNOUNCED, note_id), "w") as marker:
        marker.write(now() + "\n")
    return True


def note_result(outcome, note_id, request_id, announced):
    return {
        "schema": "fm-inbox-note.v1",
        "outcome": outcome,
        "id": note_id,
        "request_id": request_id,
        "saved": True,
        "announced": announced is True,
        "acknowledged": announced is None,
        "path": note_path(note_id),
    }


def cmd_note(args, body):
    request_id = None
    if len(args) != 4 or args[0] != "--request-id" or args[2:] != ["--json", "-"]:
        die("usage: fm-inbox.sh note [--request-id <id>] [--json] -")
    request_id = args[1]
    fault = next_fault("note")
    apply_early_fault(fault)
    if not body.strip():
        die("refusing to queue an empty note")

    os.makedirs(REQUESTS, exist_ok=True)
    reserved = os.path.join(REQUESTS, request_id)
    if os.path.exists(reserved):
        with open(reserved) as source:
            note_id = source.read().strip()
        if note_path(note_id) is None:
            write_note(note_id, request_id, body)
        outcome = "replay"
    else:
        note_id = "%d-%s" % (time.time(), secrets.token_hex(3))
        write_note(note_id, request_id, body)
        with open(reserved, "w") as target:
            target.write(note_id + "\n")
        outcome = "created"

    if "sleep_after_save" in fault:
        time.sleep(fault["sleep_after_save"])
    announced = announce(note_id, fault)
    emit(note_result(outcome, note_id, request_id, announced))
    if announced is False:
        die("note %s is saved but firstmate was NOT woken" % note_id, 3)


def cmd_announce(args):
    if len(args) != 2 or args[0] != "--json":
        die("usage: fm-inbox.sh announce [--json] <id>")
    note_id = args[1]
    fault = next_fault("announce")
    apply_early_fault(fault)
    if note_path(note_id) is None:
        die("no such note: %s" % note_id)
    already = os.path.exists(os.path.join(ANNOUNCED, note_id))
    announced = announce(note_id, fault)
    emit(note_result("replay" if already or announced is None else "created", note_id, None, announced))
    if announced is False:
        die("note %s is saved but firstmate was NOT woken" % note_id, 3)


def parse_record(path):
    with open(path, encoding="utf-8", errors="replace") as source:
        text = source.read()
    headers, _, body = text.partition("\n--\n")
    meta = dict(line.split("=", 1) for line in headers.splitlines() if "=" in line)
    return meta, body[:-1] if body.endswith("\n") else body


def list_notes(folder, acknowledged):
    if not os.path.isdir(folder):
        return []
    rows = []
    for name in sorted((n for n in os.listdir(folder) if n.endswith(".note") and not n.startswith(".")), reverse=True):
        meta, body = parse_record(os.path.join(folder, name))
        note_id = meta.get("id") or name[:-5]
        reply = None
        reply_path = os.path.join(REPLIES, note_id)
        if os.path.exists(reply_path):
            reply_meta, reply_body = parse_record(reply_path)
            reply = {"id": note_id, "at": reply_meta.get("at"), "body": reply_body, "cursor": "%012d" % int(reply_meta["seq"])}
        rows.append({
            "id": note_id,
            "at": meta.get("at"),
            "source": meta.get("source"),
            "request_id": meta.get("request_id"),
            "body": body,
            "acknowledged": acknowledged,
            "announced": os.path.exists(os.path.join(ANNOUNCED, note_id)),
            "reply": reply,
        })
    return rows


def cmd_receipts(args):
    after = ""
    all_pending = False
    while args:
        if args[0] == "--after" and len(args) > 1:
            after, args = args[1], args[2:]
        elif args[0] == "--all-pending":
            all_pending, args = True, args[1:]
        else:
            die("unknown option for receipts: %s" % args[0])
    apply_early_fault(next_fault("receipts"))

    pending = list_notes(INBOX, False)
    handled = list_notes(HANDLED, True)
    replies = sorted((row["reply"] for row in pending + handled if row["reply"]), key=lambda reply: reply["cursor"])
    if after:
        replies = [reply for reply in replies if reply["cursor"] > after]

    omitted = []
    if len(pending) > BOUND and not all_pending:
        omitted.append({"surface": "pending notes omitted by bound: %d" % (len(pending) - BOUND), "reveal": "pass --all-pending"})
        pending = pending[:BOUND]
    if len(handled) > BOUND:
        omitted.append({"surface": "handled notes omitted by bound: %d" % (len(handled) - BOUND), "reveal": "pass --all-handled"})
        handled = handled[:BOUND]
    if len(replies) > BOUND:
        omitted.append({"surface": "replies omitted by bound: %d" % (len(replies) - BOUND), "reveal": "pass --all-replies"})
        replies = replies[:BOUND]

    emit({
        "schema": "fm-inbox-receipts.v1",
        "home": "me/firstmate",
        "generated": now(),
        "pending": pending,
        "handled": handled,
        "replies": replies,
        "reply_cursor": replies[-1]["cursor"] if replies else after,
        "omitted": omitted,
    })


def cmd_ready(args):
    if args:
        die("usage: fm-inbox.sh ready")
    apply_early_fault(next_fault("ready"))
    with open(os.path.join(FAKE, "ready.json")) as source:
        sys.stdout.write(source.read())


def main():
    args = sys.argv[1:]
    subcommand = args[0] if args else ""
    body = sys.stdin.read() if subcommand == "note" else None
    record(args, body)
    if subcommand == "note":
        cmd_note(args[1:], body)
    elif subcommand == "announce":
        cmd_announce(args[1:])
    elif subcommand == "receipts":
        cmd_receipts(args[1:])
    elif subcommand == "ready":
        cmd_ready(args[1:])
    else:
        die("unknown subcommand: %s (try --help)" % subcommand)


main()
