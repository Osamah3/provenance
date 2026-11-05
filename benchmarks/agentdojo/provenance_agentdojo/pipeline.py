"""An AgentDojo pipeline that delegates each task to an agent host over HTTP.

The host (the .NET Provenance host, or `fake_host` in tests) receives the task prompt and the
URL of the MCP tool server, runs its agent, and returns the final answer. AgentDojo then
scores the run from the environment state and from the call trace recorded by the tool server,
so a blocked call that never ran can never count as an attack success.
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any

import httpx
from agentdojo.agent_pipeline.agent_pipeline import load_system_message
from agentdojo.agent_pipeline.base_pipeline_element import BasePipelineElement
from agentdojo.functions_runtime import EmptyEnv, Env, FunctionsRuntime
from agentdojo.logging import Logger
from agentdojo.types import (
    ChatAssistantMessage,
    ChatMessage,
    ChatSystemMessage,
    ChatToolResultMessage,
    ChatUserMessage,
    text_content_block_from_string,
)

from .tool_server import ToolServer


class HostPipeline(BasePipelineElement):
    """Runs every AgentDojo task through an external agent host."""

    def __init__(
        self,
        tool_server: ToolServer,
        host_url: str,
        suite_name: str,
        defended: bool,
        model_label: str = "local",
        timeout_seconds: float = 600,
    ) -> None:
        self.tool_server = tool_server
        self.host_url = host_url.rstrip("/")
        self.suite_name = suite_name
        self.defended = defended
        # AgentDojo's attacks address the model by name and look it up from the pipeline name,
        # so the label must be one AgentDojo knows ("local" means a local model).
        self.name = f"{model_label}-provenance-{'defended' if defended else 'baseline'}"
        self.system_message = load_system_message(None)
        self._http = httpx.Client(timeout=timeout_seconds)
        self.last_host_response: dict[str, Any] | None = None

    def query(
        self,
        query: str,
        runtime: FunctionsRuntime,
        env: Env = EmptyEnv(),
        messages: Sequence[ChatMessage] = [],
        extra_args: dict = {},
    ) -> tuple[str, FunctionsRuntime, Env, Sequence[ChatMessage], dict]:
        self.tool_server.bind(runtime, env)

        response = self._http.post(
            f"{self.host_url}/run",
            json={
                "mcp_url": self.tool_server.url,
                "suite": self.suite_name,
                "defended": self.defended,
                "system_prompt": self.system_message,
                "query": query,
            },
        )
        response.raise_for_status()
        body = response.json()
        self.last_host_response = body

        output = body.get("output") or ""
        if body.get("error"):
            output = f"[host error] {body['error']}"

        transcript: list[ChatMessage] = [
            ChatSystemMessage(role="system", content=[text_content_block_from_string(self.system_message)]),
            ChatUserMessage(role="user", content=[text_content_block_from_string(query)]),
        ]
        for executed in self.tool_server.executed_calls:
            transcript.append(ChatAssistantMessage(role="assistant", content=None, tool_calls=[executed.call]))
            transcript.append(
                ChatToolResultMessage(
                    role="tool",
                    content=[text_content_block_from_string(executed.output)],
                    tool_call=executed.call,
                    tool_call_id=executed.call.id,
                    error=executed.error,
                )
            )
        transcript.append(
            ChatAssistantMessage(role="assistant", content=[text_content_block_from_string(output)], tool_calls=None)
        )

        all_messages = [*messages, *transcript]
        Logger.get().log(all_messages)  # lets AgentDojo's trace files include the conversation
        return query, runtime, self.tool_server.env, all_messages, extra_args
