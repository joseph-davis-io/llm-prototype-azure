import json, sys, pathlib

path = pathlib.Path(sys.argv[1])
ok = True
with path.open("r", encoding="utf-8") as f:
    for i, line in enumerate(f, start=1):
        line = line.strip()
        if not line:
            continue
        try:
            json.loads(line)
        except Exception as e:
            print(f"[ERROR] {path}:{i} invalid JSON: {e}")
            ok = False

if not ok:
    sys.exit(1)

print(f"[OK] {path} looks like valid JSONL")