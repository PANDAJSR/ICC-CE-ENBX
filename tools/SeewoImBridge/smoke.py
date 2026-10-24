"""Offline IPC/runtime checks. Never submits a connect command or any credentials."""
import json
from pathlib import Path
import subprocess
import sys

exe = Path(__file__).parent / "bin/Release/net10.0-windows/win-x86/SeewoImBridge.exe"
sdk_args = ["--sdk", sys.argv[1]] if len(sys.argv) > 1 else []


def run(args, data=""):
    result = subprocess.run([str(exe), *args], input=data, text=True,
                            encoding="utf-8", capture_output=True, timeout=15)
    rows = [json.loads(line) for line in result.stdout.splitlines()]
    assert result.returncode == 0, (result.returncode, rows, result.stderr)
    assert not result.stderr, result.stderr
    return rows


probe = run([*sdk_args, "--probe"])
assert len(probe) == 1 and probe[0]["ok"] and probe[0]["architecture"] == "x86"
assert "rtmq_im.dll" in [name.lower() for name in probe[0]["modules"]], probe
print(json.dumps(probe[0], ensure_ascii=False))

errors = run(sdk_args, '\n'.join([
    '{"type":"send","id":302,"body":{"code":0}}',
    'not-json',
    '{"type":"connect","uid":"offline-test","token":"SECRET-MUST-NOT-APPEAR"}',
    '{"type":"unknown"}',
    '{"type":"stop"}',
    ''
]))
assert len(errors) == 4 and all(row["type"] == "error" for row in errors), errors
assert "SECRET-MUST-NOT-APPEAR" not in json.dumps(errors), errors
assert run(sdk_args) == []  # EOF closes without initializing an IM connection.
oversized = run(sdk_args, 'x' * (1024 * 1024 + 1) + '\n{"type":"stop"}\n')
assert len(oversized) == 1 and oversized[0]["type"] == "error", oversized
print("PASS: native probe, pre-connect send rejection, malformed JSON, incomplete connect, unknown command, credential non-echo, EOF, oversized line, stop")
