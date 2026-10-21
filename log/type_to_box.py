#!/usr/bin/env python3
"""Type a text file into the Paperspace window via macOS System Events, slowly.
Usage: python3 type_to_box.py file.txt [chars_per_sec] [--no-enter]"""
import sys, subprocess, time
args=[a for a in sys.argv[1:] if not a.startswith("--")]
path=args[0]; cps=float(args[1]) if len(args)>1 else 40; noenter="--no-enter" in sys.argv
def osa(*lines):
    a=[]
    for l in lines: a+=["-e",l]
    subprocess.run(["osascript",*a],check=True)
osa('tell application "Paperspace" to activate'); time.sleep(0.7)
for raw in open(path).read().splitlines():
    line=raw.rstrip()
    if not line.strip(): continue
    for i in range(0,len(line),4):
        chunk=line[i:i+4].replace("\\","\\\\").replace('"','\\"')
        osa(f'tell application "System Events" to keystroke "{chunk}"'); time.sleep(4/cps)
    time.sleep(0.3)
    if not noenter:
        osa('tell application "System Events" to key code 36'); time.sleep(0.6)
print("typed", path, "enter" if not noenter else "no-enter")
