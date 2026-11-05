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


async def run_task(request: Request) -> JSONResponse:
    body = await request.json()
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
    args = parser.parse_args()
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
