import re
import os

server_file = r"d:\\GamesMailRu\\emuserverGWT\\GwtEmulator.Protocol\\Messages.cs"
client_dir = r"d:\\GamesMailRu\\emuserverGWT\\decfilesgame\\ClientSocket"

if not os.path.exists(server_file):
    print(f"Server messages file not found: {server_file}")
    raise SystemExit(1)

server_text = open(server_file, encoding='utf-8').read()

# helper to find balanced brace block starting at first '{' after pos
def extract_block(text, start_pos):
    i = text.find('{', start_pos)
    if i == -1:
        return None, -1
    depth = 1
    j = i
    while j + 1 < len(text) and depth > 0:
        j += 1
        c = text[j]
        if c == '{': depth += 1
        elif c == '}': depth -= 1
    if depth != 0:
        return None, -1
    return text[i+1:j], j

write_regex = re.compile(r"byteArray\.Write(UShort|Short|Int|Long|Float|Byte|Bool|UTF|Bytes|Short|RoomMainTankData|TankAllAttributeStruct|LongArray|IntArray)\s*\(")
read_regex = re.compile(r"byteArray\.Read(UShort|Short|Int|Long|Float|Byte|Bool|UTF|Bytes|LongArray|IntArray)\s*\(")

# parse server messages
server_msgs = {}
for m in re.finditer(r"\[MessagePortal\(TkMsgType\.(?P<msg>\w+)\)\]\s*public class (?P<class>\w+) : BaseRequest", server_text):
    cls = m.group('class')
    search_pos = m.end()
    write_pos = server_text.find('public override void WriteByte', search_pos)
    if write_pos == -1:
        server_msgs[cls] = []
        continue
    block, end_pos = extract_block(server_text, write_pos)
    if block is None:
        server_msgs[cls] = []
        continue
    tokens = [t for t in re.findall(r"byteArray\.Write(\w+)\s*\(", block)]
    # normalize tokens
    norm = []
    for t in tokens:
        tt = t.lower()
        if tt == 'int': norm.append('int')
        elif tt == 'long': norm.append('long')
        elif tt == 'byte': norm.append('byte')
        elif tt == 'bool': norm.append('bool')
        elif tt == 'float': norm.append('float')
        elif tt == 'ushort': norm.append('ushort')
        elif tt in ('utf','bytes','short','intarray','longarray','roommaintankdata','tankallattributestruct'):
            norm.append(tt)
        else:
            norm.append(tt)
    server_msgs[cls] = norm

# parse client messages
client_msgs = {}
if not os.path.isdir(client_dir):
    print(f"Client directory not found: {client_dir}")
else:
    for fname in os.listdir(client_dir):
        if not fname.endswith('.cs'): continue
        path = os.path.join(client_dir, fname)
        txt = open(path, encoding='utf-8').read()
        cls_match = re.search(r"public class (?P<class>\w+) : BaseRequest", txt)
        if not cls_match: continue
        cls = cls_match.group('class')
        read_pos = txt.find('public override IRequest ReadByte', cls_match.end())
        if read_pos == -1:
            client_msgs[cls] = []
            continue
        block, _ = extract_block(txt, read_pos)
        if block is None:
            client_msgs[cls] = []
            continue
        tokens = [t for t in re.findall(r"byteArray\.Read(\w+)\s*\(", block)]
        norm = []
        for t in tokens:
            tt = t.lower()
            if tt == 'int': norm.append('int')
            elif tt == 'long': norm.append('long')
            elif tt == 'byte': norm.append('byte')
            elif tt == 'bool': norm.append('bool')
            elif tt == 'float': norm.append('float')
            elif tt == 'ushort': norm.append('ushort')
            elif tt in ('utf','bytes','short','intarray','longarray'):
                norm.append(tt)
            else:
                norm.append(tt)
        client_msgs[cls] = norm

# compare
all_keys = set(server_msgs.keys()) | set(client_msgs.keys())

mismatch_count = 0
ok_count = 0
for k in sorted(all_keys):
    s = server_msgs.get(k)
    c = client_msgs.get(k)
    if s is None: s = []
    if c is None: c = []
    if s == c:
        print(f"OK    : {k} -> {s}")
        ok_count += 1
    else:
        print(f"MISS  : {k}")
        print(f"  server: {s}")
        print(f"  client: {c}")
        mismatch_count += 1

print('\nSummary:')
print(f'  total messages examined: {len(all_keys)}')
print(f'  ok: {ok_count}')
print(f'  mismatches: {mismatch_count}')

if mismatch_count > 0:
    exit(2)
else:
    exit(0)
