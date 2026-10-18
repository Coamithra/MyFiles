"""Drive a running Debug build of MyFiles through its automation pipe.

Command line (prints the result as JSON, exits 1 on an error):
    python .claude/scripts/files_auto.py help
    python .claude/scripts/files_auto.py navigate path=C:\\Windows
    python .claude/scripts/files_auto.py select 'names=["notepad.exe"]'
    python .claude/scripts/files_auto.py --json '{"values": {"ShowHiddenItems": true}}' settings.set

key=value arguments become params; a value that parses as JSON (numbers, true/false, arrays) is used as
JSON, anything else as a string. --json gives the whole params object instead.

From Python:
    from files_auto import call
    call("navigate", {"path": "C:\\Windows"})

The pipe appears once the app has started; call() waits for it up to --timeout seconds.
"""

import argparse
import json
import sys
import time


class AutomationError(Exception):
    pass


def call(method, params=None, environment="Dev", timeout=30.0):
    path = rf"\\.\pipe\Files.Automation.{environment}"
    deadline = time.monotonic() + timeout
    while True:
        try:
            pipe = open(path, "r+b", buffering=0)
            break
        except OSError:
            # Not created yet (app still starting) or all instances busy.
            if time.monotonic() > deadline:
                raise AutomationError(
                    f"No automation pipe {path} after {timeout:.0f} s. Is a Debug build of the app running?"
                )
            time.sleep(0.25)

    with pipe:
        request = {"id": 1, "method": method, "params": params or {}}
        pipe.write((json.dumps(request) + "\n").encode("utf-8"))
        response = b""
        while not response.endswith(b"\n"):
            chunk = pipe.read(65536)
            if not chunk:
                break
            response += chunk

    if not response:
        raise AutomationError("The app closed the connection without answering.")

    message = json.loads(response)
    if "error" in message:
        raise AutomationError(message["error"])
    return message.get("result")


def _parse_params(pairs):
    params = {}
    for pair in pairs:
        key, separator, value = pair.partition("=")
        if not separator:
            raise SystemExit(f"Expected key=value, got {pair!r}")
        try:
            params[key] = json.loads(value)
        except json.JSONDecodeError:
            params[key] = value
    return params


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("method")
    parser.add_argument("params", nargs="*", help="key=value pairs")
    parser.add_argument("--json", help="params as one JSON object")
    parser.add_argument("--env", default="Dev", help="app environment in the pipe name (default Dev)")
    parser.add_argument("--timeout", type=float, default=30.0, help="seconds to wait for the pipe")
    args = parser.parse_args()

    params = json.loads(args.json) if args.json else _parse_params(args.params)
    try:
        result = call(args.method, params, args.env, args.timeout)
    except AutomationError as error:
        print(error, file=sys.stderr)
        return 1

    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
