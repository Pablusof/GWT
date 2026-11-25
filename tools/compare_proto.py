#!/usr/bin/env python3
"""Compare server WriteByte vs client ReadByte keyed on TkMsgType.

Focus: server->client messages (NM_HC_/NM_SC_/NM_RC_/NM_LC_/NM_WHC_/NM_RS_...).
A mismatch in field type *sequence* means the client will misread the stream,
which can cascade into OOM (huge List<>(garbageCount)) and a dead receive thread.
"""
import re
import os
import sys

SERVER = r"d:\GamesMailRu\emuserverGWT\GwtEmulator.Protocol\Messages.cs"
CLIENT_DIRS = [
    r"d:\GamesMailRu\emuserverGWT\decfilesgame\ClientSocket",
]

# Map raw Write*/Read* call -> normalized token. Loop-bearing types are flagged.
WRITE_MAP = {
    'WriteByte': 'byte', 'WriteBool': 'byte', 'WriteShort': 'short', 'WriteUShort': 'ushort',
    'WriteInt': 'int', 'WriteUInt': 'int', 'WriteLong': 'long', 'WriteULong': 'long',
    'WriteFloat': 'float', 'WriteUTF': 'utf', 'WriteBytes': 'bytes',
}
READ_MAP = {
    'ReadByte': 'byte', 'ReadBool': 'byte', 'ReadShort': 'short', 'ReadUShort': 'ushort',
    'ReadInt': 'int', 'ReadUInt': 'int', 'ReadLong': 'long', 'ReadULong': 'long',
    'ReadFloat': 'float', 'ReadUTF': 'utf', 'ReadBytes': 'bytes',
}

def extract_block(text, start):
    i = text.find('{', start)
    if i == -1:
        return None, -1
    depth, j = 1, i
    while j + 1 < len(text) and depth > 0:
        j += 1
        c = text[j]
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
    return text[i+1:j], j

def tokens_for(body, mapping):
    """Return list of (token, has_loop_marker). We keep it simple: sequence of tokens,
    plus mark whether a for/while loop appears (variable-length section)."""
    toks = []
    for m in re.finditer(r"byteArray\.(\w+)\s*\(", body):
        name = m.group(1)
        if name in mapping:
            toks.append(mapping[name])
    has_loop = bool(re.search(r"\b(for|while)\b", body))
    return toks, has_loop

def parse_server():
    text = open(SERVER, encoding='utf-8').read()
    out = {}  # msgtype -> (class, write_tokens, has_loop)
    for m in re.finditer(r"\[MessagePortal\(TkMsgType\.(\w+)\)\]\s*public class (\w+)\s*:\s*BaseRequest", text):
        msg, cls = m.group(1), m.group(2)
        wpos = text.find('public override void WriteByte', m.end())
        # bound to next class
        nxt = text.find('[MessagePortal', m.end())
        if wpos == -1 or (nxt != -1 and wpos > nxt):
            out[msg] = (cls, [], False)
            continue
        body, _ = extract_block(text, wpos)
        toks, loop = tokens_for(body or '', WRITE_MAP)
        out[msg] = (cls, toks, loop)
    return out

def parse_client():
    out = {}  # msgtype -> (class, read_tokens, has_loop)
    for d in CLIENT_DIRS:
        if not os.path.isdir(d):
            continue
        for fn in os.listdir(d):
            if not fn.endswith('.cs'):
                continue
            text = open(os.path.join(d, fn), encoding='utf-8', errors='ignore').read()
            mm = re.search(r"\[MessagePortal\(TkMsgType\.(\w+)\)\]\s*public class (\w+)\s*:\s*BaseRequest", text)
            if not mm:
                continue
            msg, cls = mm.group(1), mm.group(2)
            rpos = text.find('public override IRequest ReadByte')
            if rpos == -1:
                out[msg] = (cls, [], False)
                continue
            body, _ = extract_block(text, rpos)
            toks, loop = tokens_for(body or '', READ_MAP)
            out[msg] = (cls, toks, loop)
    return out

def is_server_to_client(msg):
    # server->client prefixes the client READS
    return bool(re.match(r"NM_(HC|SC|RC|LC|WHC|RS|RW|MC|MHC|MH)_", msg))

def main():
    srv = parse_server()
    cli = parse_client()
    problems = []
    only_compare = is_server_to_client
    for msg, (ccls, ctoks, cloop) in sorted(cli.items()):
        if not only_compare(msg):
            continue
        if msg not in srv:
            problems.append((msg, ccls, '<server missing>', ctoks, None, None))
            continue
        scls, stoks, sloop = srv[msg]
        # Compare leading token prefix up to min length; if mismatch in prefix -> hard desync
        n = min(len(stoks), len(ctoks))
        prefix_ok = stoks[:n] == ctoks[:n]
        same = (stoks == ctoks)
        if not same:
            # Loops legitimately make tail differ (array contents), but the *prefix* must align.
            kind = 'PREFIX-DESYNC' if not prefix_ok else 'LEN-DIFF'
            problems.append((msg, scls, ccls, stoks, ctoks, kind))
    # Report
    hard = [p for p in problems if p[5] == 'PREFIX-DESYNC']
    soft = [p for p in problems if p[5] == 'LEN-DIFF']
    missing = [p for p in problems if p[5] is None]
    print("=" * 70)
    print(f"HARD PREFIX DESYNC (client misreads stream): {len(hard)}")
    print("=" * 70)
    for msg, scls, ccls, st, ct, _ in hard:
        print(f"\n{msg}\n  server {scls}: {st}\n  client {ccls}: {ct}")
    print("\n" + "=" * 70)
    print(f"LENGTH DIFF (prefix aligns; check loops/trailing): {len(soft)}")
    print("=" * 70)
    for msg, scls, ccls, st, ct, _ in soft:
        print(f"\n{msg}\n  server {scls}: {st}\n  client {ccls}: {ct}")
    print("\n" + "=" * 70)
    print(f"CLIENT EXPECTS MSG, SERVER HAS NO CLASS: {len(missing)}")
    print("=" * 70)
    for msg, ccls, _, ct, _, _ in missing:
        print(f"  {msg} (client {ccls}: {ct})")

if __name__ == '__main__':
    main()
