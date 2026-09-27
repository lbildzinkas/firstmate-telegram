#!/usr/bin/env python3
"""A stand-in for FirstMate's snapshot scripts, installed as fm-bearings-snapshot.sh and
fm-fleet-snapshot.sh in a throwaway FirstMate home for tests.

It prints the fixture named after the script (fake/bearings.json or fake/fleet.json) for
`--json`, records every call in fake/calls.jsonl, and scripts faults in
fake/faults/bearings.json or fake/faults/fleet.json: a JSON list, one entry used per call:

  {"exit": 3, "stderr": "..."}       fail the way away mode refuses the bearings call
  {"exit": 1, "stderr": "..."}       fail with a reason on standard error
  {"sleep": 5}                       hang, so the caller times out
  {"stdout": "...", "exit": 0}       print this instead of the fixture
"""
import json
import os
import sys
import time

HOME = os.environ["FM_HOME"]
FAKE = os.path.join(HOME, "fake")
NAME = os.path.basename(sys.argv[0])
KEY = "bearings" if "bearings" in NAME else "fleet"


def record(argv):
    os.makedirs(FAKE, exist_ok=True)
    with open(os.path.join(FAKE, "calls.jsonl"), "a") as calls:
        calls.write(json.dumps({"argv": argv, "stdin": None, "fm_home": HOME, "script": KEY}) + "\n")


def next_fault():
    path = os.path.join(FAKE, "faults", KEY + ".json")
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


def main():
    args = sys.argv[1:]
    record(args)
    fault = next_fault()
    if "sleep" in fault:
        time.sleep(fault["sleep"])
    if "stdout" in fault:
        sys.stdout.write(fault["stdout"])
        sys.exit(fault.get("exit", 0))
    if "exit" in fault:
        sys.stderr.write("%s\n" % fault.get("stderr", "scripted failure"))
        sys.exit(fault["exit"])
    if args != ["--json"]:
        sys.stderr.write("usage: %s --json\n" % NAME)
        sys.exit(2)
    with open(os.path.join(FAKE, KEY + ".json")) as source:
        sys.stdout.write(source.read())


main()
