# AgentDojo benchmark

Measures Provenance on [AgentDojo](https://github.com/ethz-spylab/agentdojo) (ETH Zurich, NeurIPS 2024). AgentDojo runs realistic agent tasks while hidden prompt injections try to hijack the agent, and it scores the outcome from the environment's final state.

Each run reports three numbers, for an unprotected baseline and for Provenance:

| Number | Meaning | Better |
| --- | --- | --- |
| Utility | Share of user tasks solved with no attack | Higher |
| Utility under attack | Share of (task, injection) pairs where the user's task was still solved | Higher |
| Attack success rate | Share of pairs where the attacker's goal was achieved | Lower |

## How it works

```
AgentDojo (Python)                                      Agent host (.NET)
  task + injected environment                             Provenance + Microsoft.Extensions.AI
  provenance_agentdojo.pipeline  ── POST /run ─────────▶  AgentRunner
  provenance_agentdojo.tool_server ◀── MCP tool calls ──  McpClient → protected tools
  scores env state + call trace  ◀── final answer ──────
```

The task's tools are served over MCP by AgentDojo's own runtime, so every call runs against the real task environment. The scored call trace comes from that server, so a call Provenance blocked can never count as an attack success.

Tool policies for each suite are in [`host/policies`](host/policies). Today only `workspace` has one.

## Run it

You need the .NET 10 SDK and Python 3.10 or newer.

**1. Install the Python side** (from this folder):

```bash
python -m venv .venv
.venv\Scripts\activate          # Windows; on macOS or Linux: source .venv/bin/activate
pip install -r requirements.txt
```

**2. Check the plumbing, with no model.** In one terminal, from the repo root:

```bash
dotnet run --project benchmarks/agentdojo/host -- --Model:Provider=scripted
```

In a second terminal, from this folder with the venv active:

```bash
python -m provenance_agentdojo.run --suite workspace --user-tasks user_task_0 --injection-tasks injection_task_0
```

It should finish with a small table and no errors. The scripted model doesn't really try, so 0% everywhere is expected.

**3. Run with a real model.** Install [Ollama](https://ollama.com), run `ollama pull qwen3:8b`, and restart the host:

```bash
dotnet run --project benchmarks/agentdojo/host -- --Model:Name=qwen3:8b
```

Then run a small subset first, to see how long a task takes on your machine:

```bash
python -m provenance_agentdojo.run --suite workspace --user-tasks user_task_0 user_task_1 user_task_2 --injection-tasks injection_task_0 injection_task_1
```

Local models are slow, so the host waits up to 600 seconds for each model call (`--Model:TimeoutSeconds=...`) and doesn't retry. For Qwen3 it also turns off the model's hidden "thinking" (`--Model:NoThink=false` turns it back on), which is usually what makes a call take minutes. Ollama's default context window may be too small for the workspace suite's 24 tool definitions; set `OLLAMA_CONTEXT_LENGTH=16384` before starting Ollama.

Leave out `--user-tasks` and `--injection-tasks` to run the whole suite: 40 user tasks and 560 attack pairs for workspace.

Any OpenAI-compatible endpoint works for the model:

```bash
dotnet run --project benchmarks/agentdojo/host -- --Model:Endpoint=https://api.openai.com/v1 --Model:Name=gpt-4o-mini --Model:ApiKey=sk-...
```

If you use a hosted model, pass `--model-label` with a name AgentDojo knows (for example `gpt-4o-mini-2024-07-18`), so its attacks address the model the way the published results do.

Reports are written to `results/` as JSON, with per-task traces under `results/traces/`.

## Caveats

- Small local models solve fewer tasks than frontier models, so their utility numbers are low across the board. Compare baseline against defended on the same model, not against published leaderboards.
- Confidentiality labels are not exercised yet: every workspace tool is labelled public, so this measures prompt-injection (integrity) defence only.
