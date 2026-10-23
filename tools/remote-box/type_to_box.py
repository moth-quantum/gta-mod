#!/usr/bin/env python3
"""Type a text file into the Paperspace window via macOS System Events.
Refuses to type unless Paperspace is the frontmost app (so keystrokes never land elsewhere).
Usage: python3 type_to_box.py file.txt [chars_per_sec] [--no-enter]"""
import sys, subprocess, time
args=[a for a in sys.argv[1:] if not a.startswith("--")]
path=args[0]; cps=float(args[1]) if len(args)>1 else 40; noenter="--no-enter" in sys.argv
SPECIAL={"+":24,"*":28,"#":20,"~":50,"|":42,"_":27,"{":33,"}":30,":":41,'"':39,"<":43,">":47,"?":44,"!":18,"@":19,"$":21,"%":23,"^":22,"&":26,"(":25,")":29}
def osa(*lines):
    a=[]
    for l in lines: a+=["-e",l]
    return subprocess.run(["osascript",*a],check=True,capture_output=True,text=True).stdout.strip()
def front():
    return osa('tell application "System Events" to get name of first application process whose frontmost is true')
osa('tell application "Paperspace" to activate'); time.sleep(0.8)
if front()!="Paperspace": sys.exit(f"ABORT: frontmost app is {front()!r}, not Paperspace")
def send_chunk(chunk):
    if front()!="Paperspace": sys.exit("ABORT mid-line: focus lost to "+front())
    esc=chunk.replace("\\","\\\\").replace('"','\\"')
    osa(f'tell application "System Events" to keystroke "{esc}"')
for raw in open(path).read().splitlines():
    line=raw.rstrip("\n")
    if not line.strip(): continue
    buf=""
    for ch in line:
        if ch in SPECIAL:
            if buf: send_chunk(buf); time.sleep(len(buf)/cps); buf=""
            if front()!="Paperspace": sys.exit("ABORT: focus lost")
            osa(f'tell application "System Events" to key code {SPECIAL[ch]} using shift down'); time.sleep(2/cps)
        else:
            buf+=ch
            if len(buf)>=4: send_chunk(buf); time.sleep(4/cps); buf=""
    if buf: send_chunk(buf); time.sleep(len(buf)/cps)
    time.sleep(0.3)
    if not noenter:
        if front()!="Paperspace": sys.exit("ABORT before enter: focus lost")
        osa('tell application "System Events" to key code 36'); time.sleep(0.6)
print("typed", path, "enter" if not noenter else "no-enter")
