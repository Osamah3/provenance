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


# Some models (gpt-oss in particular) write typographic characters: a non-breaking hyphen in
# "mainsail\u2011tech.com", a narrow no-break space in "May\u202f26". AgentDojo grades answers by
# exact substring match, so a correct answer fails on typography alone. We map them to plain ASCII
# for both baseline and defended runs, so the comparison stays fair.
_TYPOGRAPHY = str.maketrans(
    {
        "\u2010": "-",  # hyphen
        "\u2011": "-",  # non-breaking hyphen
        "\u2012": "-",  # figure dash
        "\u2013": "-",  # en dash
        "\u2212": "-",  # minus sign
        "\u00a0": " ",  # no-break space
        "\u202f": " ",  # narrow no-break space
        "\u2009": " ",  # thin space
    }
)


def normalize_typography(text: str) -> str:
    return text.translate(_TYPOGRAPHY)


class HostPipeline(BasePipelineElement):
    """Runs every AgentDojo task through an external agent host."""

    def __init__(
        self,
        tool_server: ToolServer,
        host_url: str,
        suite_name: str,
        defended: bool,
        model_label: str = "local",
        timeout_seconds: float = 1800,  # a whole task: several slow local model calls
        wait_for_rate_limits: bool = False,
        typed_answers: bool = False,
    ) -> None:
        self.tool_server = tool_server
        self.host_url = host_url.rstrip("/")
        self.suite_name = suite_name
        self.defended = defended
        # AgentDojo's attacks address the model by name and look it up from the pipeline name,
        # so the label must be one AgentDojo knows ("local" means a local model).
        self.typed_answers = defended and typed_answers
        variant = "baseline" if not defended else ("defended-typed" if self.typed_answers else "defended")
        self.name = f"{model_label}-provenance-{variant}"
        self.system_message = load_system_message(None)
        self.wait_for_rate_limits = wait_for_rate_limits
        # When the host waits out rate limits, one task can take hours: don't time it out.
        self._http = httpx.Client(timeout=None if wait_for_rate_limits else timeout_seconds)
        self.last_host_response: dict[str, Any] | None = None
        self.host_errors: list[str] = []
        # After this many host errors in a row (typically a daily rate limit), stop calling the
        # host and fail the remaining tasks fast; --resume runs them later.
        self.give_up_after = 3
        self._errors_in_a_row = 0
        self.tasks_run = 0

    def query(
        self,
        query: str,
        runtime: FunctionsRuntime,
        env: Env = EmptyEnv(),
        messages: Sequence[ChatMessage] = [],
        extra_args: dict = {},
    ) -> tuple[str, FunctionsRuntime, Env, Sequence[ChatMessage], dict]:
        self.tool_server.bind(runtime, env)

        if self._errors_in_a_row >= self.give_up_after:
            body: dict[str, Any] = {"error": f"skipped after {self.give_up_after} host errors in a row"}
        else:
            try:
                response = self._http.post(
                    f"{self.host_url}/run",
                    json={
                        "mcp_url": self.tool_server.url,
                        "suite": self.suite_name,
                        "defended": self.defended,
                        "system_prompt": self.system_message,
                        "query": query,
                        "wait_for_rate_limits": self.wait_for_rate_limits,
                        "typed_answers": self.typed_answers,
                    },
                )
                response.raise_for_status()
                body = response.json()
            except httpx.HTTPError as exc:
                # A host that hangs or drops the connection fails this task, not the whole run.
                body = {"error": f"bridge could not get an answer from the host: {type(exc).__name__}: {exc}"}
        self.tasks_run += 1
        self.last_host_response = body

        output = normalize_typography(body.get("output") or "")
        if body.get("error"):
            self.host_errors.append(body["error"])
            self._errors_in_a_row += 1
            output = f"[host error] {body['error']}"
        else:
            self._errors_in_a_row = 0

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
        logger = Logger.get()
        logger.log(all_messages)  # lets AgentDojo's trace files include the conversation
        if hasattr(logger, "set_contextarg"):
            # Provenance's allow/block decisions, so a trace shows why a defended run did what it did.
            logger.set_contextarg("provenance_audit", body.get("audit"))
        return query, runtime, self.tool_server.env, all_messages, extra_args
