"""Exposes an AgentDojo task's tools over MCP (Streamable HTTP), so an agent written in any
language can use them while AgentDojo keeps scoring the environment as usual.

One server runs for the whole benchmark. Before each task, the pipeline binds the task's
runtime and environment; every tool call then runs against that environment through
AgentDojo's own `FunctionsRuntime`, and is recorded as the authoritative call trace.
"""

from __future__ import annotations

import socket
import threading
import time
import uuid
from dataclasses import dataclass, field
from typing import Any

import uvicorn
from agentdojo.agent_pipeline.tool_execution import tool_result_to_str
from agentdojo.functions_runtime import EmptyEnv, Env, FunctionCall, FunctionsRuntime
from mcp import types
from mcp.server.lowlevel import Server


@dataclass
class ExecutedCall:
    """A tool call that actually ran against the environment."""

    call: FunctionCall
    output: str
    error: str | None


@dataclass
class _Binding:
    runtime: FunctionsRuntime = field(default_factory=lambda: FunctionsRuntime([]))
    env: Env = field(default_factory=EmptyEnv)
    calls: list[ExecutedCall] = field(default_factory=list)


class ToolServer:
    """An MCP server whose tools are the functions of the currently bound AgentDojo task."""

    def __init__(self, host: str = "127.0.0.1", port: int | None = None) -> None:
        self.host = host
        self.port = port or _free_port(host)
        self._binding = _Binding()
        self._lock = threading.Lock()
        self._thread: threading.Thread | None = None
        self._uvicorn: uvicorn.Server | None = None

        self._server = Server(
            "agentdojo-tools",
            version="1.0",
            on_list_tools=self._list_tools,
            on_call_tool=self._call_tool,
        )

    @property
    def url(self) -> str:
        return f"http://{self.host}:{self.port}/mcp"

    def bind(self, runtime: FunctionsRuntime, env: Env) -> None:
        """Points the tools at a new task's runtime and environment, and clears the trace."""
        with self._lock:
            self._binding = _Binding(runtime=runtime, env=env)

    @property
    def executed_calls(self) -> list[ExecutedCall]:
        with self._lock:
            return list(self._binding.calls)

    @property
    def env(self) -> Env:
        with self._lock:
            return self._binding.env

    # --- MCP handlers -------------------------------------------------------------------

    async def _list_tools(self, ctx: Any, params: types.PaginatedRequestParams | None) -> types.ListToolsResult:
        with self._lock:
            functions = list(self._binding.runtime.functions.values())
        return types.ListToolsResult(
            tools=[
                types.Tool(
                    name=f.name,
                    description=f.description,
                    input_schema=f.parameters.model_json_schema(),
                )
                for f in functions
            ]
        )

    async def _call_tool(self, ctx: Any, params: types.CallToolRequestParams) -> types.CallToolResult:
        arguments = dict(params.arguments or {})
        with self._lock:
            binding = self._binding
            result, error = binding.runtime.run_function(binding.env, params.name, arguments)
            output = error if error is not None else tool_result_to_str(result)
            binding.calls.append(
                ExecutedCall(
                    call=FunctionCall(function=params.name, args=arguments, id=f"call_{uuid.uuid4().hex[:12]}"),
                    output=output,
                    error=error,
                )
            )
        return types.CallToolResult(
            content=[types.TextContent(type="text", text=output)],
            is_error=error is not None,
        )

    # --- Lifecycle ----------------------------------------------------------------------

    def start(self) -> "ToolServer":
        app = self._server.streamable_http_app(host=self.host, stateless_http=True, json_response=True)
        config = uvicorn.Config(app, host=self.host, port=self.port, log_level="warning")
        self._uvicorn = uvicorn.Server(config)
        self._thread = threading.Thread(target=self._uvicorn.run, name="agentdojo-mcp", daemon=True)
        self._thread.start()
        deadline = time.monotonic() + 10
        while not self._uvicorn.started:
            if time.monotonic() > deadline:
                raise RuntimeError("The MCP tool server did not start within 10 seconds.")
            time.sleep(0.05)
        return self

    def stop(self) -> None:
        if self._uvicorn is not None:
            self._uvicorn.should_exit = True
        if self._thread is not None:
            self._thread.join(timeout=5)

    def __enter__(self) -> "ToolServer":
        return self.start()

    def __exit__(self, *exc: object) -> None:
        self.stop()


def _free_port(host: str) -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind((host, 0))
        return s.getsockname()[1]
