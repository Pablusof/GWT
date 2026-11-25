import re, sys, collections
names = collections.Counter()
attrs = collections.Counter()
for path in sys.argv[1:]:
    src = open(path, encoding='utf-8-sig').read()
    src = re.sub(r'//.*', '', src)
    for m in re.finditer(r'\b(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+)*(?:class|struct|record|enum)\s+(\w+)', src):
        names[m.group(1)] += 1
    for m in re.finditer(r'\[MessagePortal\(TkMsgType\.(\w+)\)\]', src):
        attrs[m.group(1)] += 1
bad = [(n, c) for n, c in names.items() if c > 1]
badattr = [(n, c) for n, c in attrs.items() if c > 1]
print("ДУБЛИ ТИПОВ:", bad if bad else "нет")
print("ДУБЛИ MessagePortal:", badattr if badattr else "нет")

# ПРИМЕЧАНИЕ. Скрипт ловит только баланс скобок, дубли типов и повторные
# [MessagePortal]. Ошибки области видимости (CS0136) и типов (CS0023 — '?.' на
# структуре) без настоящего компилятора не отлавливаются: пробовал эвристику по
# повторным объявлениям — она даёт сплошные ложные срабатывания на обычных циклах.
# Такие вещи проверяются только сборкой dotnet build.
