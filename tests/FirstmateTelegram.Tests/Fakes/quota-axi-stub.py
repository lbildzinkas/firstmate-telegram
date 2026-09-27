#!/usr/bin/env python3
"""A stand-in for quota-axi, installed in a throwaway FirstMate home for tests.

It prints the fixture named in fake/quota.json one directory above the script (a
captured quota-axi snapshot, see Fixtures/quota-axi/) and exits 1 when no fixture
is set, which quota readers must treat as unknown. It never touches a credential
or the network, and it reads no environment: quota-axi itself is called without FM_HOME.
"""
import os
import sys

home = os.path.dirname(os.path.dirname(os.path.realpath(__file__)))
path = os.path.join(home, "fake", "quota.json")
if not os.path.exists(path):
    sys.stderr.write("quota-axi-stub: no quota fixture set\n")
    sys.exit(1)
with open(path) as source:
    sys.stdout.write(source.read())
