"""Read-only bridge to the local IDA MCP when the connector is unavailable."""
import json
import sys
import urllib.request

method = "tools/list" if len(sys.argv) == 1 else "tools/call"
params = {} if method == "tools/list" else {"name": sys.argv[1], "arguments": json.load(sys.stdin)}
if method == "tools/call" and params["name"] not in {"lookup_funcs", "decompile", "func_query", "xrefs_to"}:
    raise SystemExit("read-only tool not allowed")
request = urllib.request.Request(
    "http://127.0.0.1:13337/mcp",
    data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode(),
    headers={"Content-Type": "application/json"},
)
response = json.load(urllib.request.urlopen(request, timeout=30))
if method == "tools/list":
    response = [t for t in response.get("result", {}).get("tools", [])
                if t["name"] in {"lookup_funcs", "decompile", "func_query", "xrefs_to"}]
print(json.dumps(response, indent=2))
