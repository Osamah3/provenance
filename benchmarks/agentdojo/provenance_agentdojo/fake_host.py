"""A stand-in for the .NET host, used to test the bridge without .NET or a model.

Its "agent" is trivial and deterministic: it lists the MCP tools, calls `get_current_day`
if the suite has it, and reports the result. It speaks the same /run protocol as the real host.

    python -m provenance_agentdojo.fake_host --port 5081
"""

from __future__ import annotations

import argparse

import uvicorn
from mcp import Client
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route


# --fail-after N: answer N tasks, then fail every task as a rate-limited host would (tests --resume).
_state = {"fail_after": None, "answered": 0}


async def run_task(request: Request) -> JSONResponse:
    body = await request.json()
    if _state["fail_after"] is not None and _state["answered"] >= _state["fail_after"]:
        return JSONResponse({"output": None, "audit": [], "error": "simulated rate limit"})
    _state["answered"] += 1
    try:
        async with Client(body["mcp_url"]) as client:
            tools = await client.list_tools()
            names = [tool.name for tool in tools.tools]
            if "get_current_day" in names:
                result = await client.call_tool("get_current_day", {})
                text = "".join(getattr(block, "text", "") for block in result.content)
                output = f"Today is {text}."
            else:
                output = f"I can see {len(names)} tools."
        return JSONResponse({"output": output, "audit": [], "error": None})
    except Exception as exc:  # report, don't crash the benchmark
        return JSONResponse({"output": None, "audit": [], "error": f"{type(exc).__name__}: {exc}"})


app = Starlette(routes=[Route("/run", run_task, methods=["POST"])])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=5081)
    parser.add_argument("--fail-after", type=int, default=None, help="Fail every task after this many")
    args = parser.parse_args()
    _state["fail_after"] = args.fail_after
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
