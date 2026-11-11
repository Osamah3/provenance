# Provenance

[![NuGet](https://img.shields.io/nuget/vpre/Provenance)](https://www.nuget.org/packages/Provenance) [![CI](https://github.com/Osamah3/provenance/actions/workflows/ci.yml/badge.svg)](https://github.com/Osamah3/provenance/actions/workflows/ci.yml)

Deterministic prompt-injection defence for .NET agents built on [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai).

An agent that reads untrusted content (issues, emails, web pages) and can also act (post, send, write) can be hijacked by instructions hidden in that content. Better prompts don't fix this reliably. Provenance enforces it in code instead: every piece of content carries a label, and a tool can only run if the labels in play allow it.

> **Status: pre-alpha.** The API will change. Early benchmark results are below; the sample is small.

## What it stops

| Attack | What happens |
| --- | --- |
| Injected instructions in a tool result | Untrusted results are hidden from the model behind a `var_…` reference, so the planner never reads them |
| The model reveals the content anyway | The conversation becomes untrusted, and every tool that doesn't accept untrusted input is blocked from then on |
| Hidden content passed into a tool argument | The argument carries the untrusted label, and the call is blocked |
| Private data sent to a public destination | Blocked by the destination's confidentiality cap |
| An unwrapped tool slipped into the request | The whole request is refused before the model is called |

## Early results

[AgentDojo](https://github.com/ethz-spylab/agentdojo) v1.2.2, `workspace` suite, `important_instructions` attack, model `openai/gpt-oss-120b` (via Groq), November 2026. Same model, tools and tasks in every row; only the defence changes.

| Defence | Utility (no attack) | Utility under attack | Attack success |
| --- | --- | --- | --- |
| None | 100% (5/5) | 50% (5/10) | 20% (2/10) |
| Provenance, strict | 60% (3/5) | 60% (6/10) | **0% (0/10)** |
| Provenance, typed answers | 80% (4/5) | 80% (8/10) | **0% (0/10)** |

**Read this as an early signal, not a headline number:** it covers 5 of the suite's 40 user tasks (0, 3, 6, 7, 14) and 2 of its 14 injection tasks (0, 3), run once each. Strict mode loses utility on tasks that must act on what untrusted content says ("am I free at noon? then book lunch"). [Typed answers](#typed-answers-opt-in) win most of it back. The remaining typed failure is a reschedule that needs an event id from hidden data, where the agent created a new event instead. Method, deviations and how to reproduce: [`benchmarks/agentdojo`](benchmarks/agentdojo).

## Quick start

```
dotnet add package Provenance --prerelease
```

```csharp
using Microsoft.Extensions.AI;
using Provenance;

var session = new ProvenanceSession(new ProvenanceOptions
{
    QuarantineClient = quarantineModel, // a plain IChatClient with no tools
});

IList<AITool> tools =
[
    session.Protect(readIssue, ToolPolicy.UntrustedSource),
    session.Protect(readFile, new ToolPolicy { SourceIntegrity = Integrity.Trusted, Confidentiality = Confidentiality.Private }),
    session.Protect(postComment, ToolPolicy.PublicSink),
    .. session.CreateSecurityTools(), // quarantined_llm, inspect_variable
];

IChatClient agent = new ChatClientBuilder(model)
    .UseProvenance(session)      // refuses unprotected tools
    .UseFunctionInvocation()
    .Build();

var response = await agent.GetResponseAsync(messages, new ChatOptions { Tools = tools });
Console.WriteLine(session.RenderForUser(response.Text));
```

Run the demo, which needs no API key:

```bash
dotnet run --project samples/IssueTriage
```

## How it works

Labels have two dimensions. Combining labels always keeps the most restrictive value of each.

- **Integrity:** `trusted` or `untrusted`. Untrusted wins.
- **Confidentiality:** `public` < `private` < `user_identity`. The higher value wins.

Each tool has a policy:

| Key | Meaning |
| --- | --- |
| `source_integrity` | The label given to what the tool returns. Undeclared means untrusted. |
| `confidentiality` | How sensitive the tool's output is. |
| `max_allowed_confidentiality` | The most sensitive data the tool may receive. Set it on every tool that sends data anywhere. |
| `accepts_untrusted` | Whether the tool may run once the conversation is untrusted. Leave this off for side effects. |

Policies can be passed to `Protect`, or declared in a tool's `AdditionalProperties` with the keys above. A malformed policy blocks the tool rather than being ignored.

The labels, policy keys and variable-reference format match [FIDES](https://arxiv.org/abs/2505.23643) in Microsoft Agent Framework (Python). Per-result labels from IFC-aware MCP servers (`_meta.ifc`, as emitted by GitHub's MCP server) are read with `LabelWire.TryReadIfcMeta`.

### MCP tools

`Provenance.Mcp` protects every tool of an MCP server in one call:

```
dotnet add package Provenance.Mcp --prerelease
```

```csharp
using Provenance.Mcp;

var github = await McpClient.CreateAsync(githubTransport);
var files = await McpClient.CreateAsync(filesTransport);

IList<AITool> tools =
[
    .. await github.ListProtectedToolsAsync(session),                  // untrusted by default
    .. await files.ListProtectedToolsAsync(session, new McpProvenanceOptions
    {
        ServerIntegrity = Integrity.Trusted,                           // a server you run yourself
        Policies = { ["delete_file"] = ToolPolicy.PublicSink },        // override any tool by name
    }),
    .. session.CreateSecurityTools(),
];
```

Policies come from each tool's annotations, read conservatively because the server writes them: results are untrusted unless you say the server is trusted, `openWorldHint: true` tools are always untrusted, no MCP tool may receive private data, and none runs once the conversation is untrusted (`AllowReadOnlyToolsInUntrustedContext` relaxes this for read-only, closed-world tools). Labels that IFC-aware servers attach to each result (`_meta.ifc`, as GitHub's MCP server does) are applied automatically; they can only make a result stricter unless you set `TrustServerLabels`.

### Typed answers (opt-in)

In strict mode an agent that needs to *decide* something from untrusted content, such as "am I free at noon? then book lunch", gets stuck: reading the content makes the conversation untrusted, and every action after that is blocked. Set `AllowTypedQuarantineAnswers = true` and `quarantined_llm` takes an `output_type` of `boolean`, `integer`, `number`, `date`, `datetime` or `choice` (one of the options the agent lists). An answer that parses exactly is returned to the agent in the clear, labelled trusted with the confidentiality of its inputs, so the agent can act on it. Anything that does not parse stays hidden.

The trade-off: a typed answer cannot carry instructions, but an attacker who controls the hidden data can still influence *which* allowed value comes back, for example flip a yes/no or choose a date. That is why it is off by default.

Every decision is recorded in `session.AuditLog` and as an OpenTelemetry span; subscribe with `.AddSource("Provenance")`.

## Limitations (today)

- Only top-level arguments and string arrays are scanned for hidden-variable ids; nested objects are not.
- `user_identity` data is blocked at any capped sink, because principal checks are not implemented yet.
- Approvals, per-item labels and persistence across process restarts are not implemented.
- Streaming works, but the guard checks only the tools, not content mid-stream.
- Keep `FunctionInvokingChatClient.IncludeDetailedErrors` off: tool exception messages are not labelled.

## Roadmap

1. AgentDojo benchmark at full scale (all workspace tasks and attacks, more models, the other three suites). [Early results](#early-results) cover a small sample.
2. Microsoft Agent Framework adapter (`AIAgentBuilder`).
3. Human approval for policy violations, bound to the exact call.

## Credits

The design follows [FIDES](https://arxiv.org/abs/2505.23643) (Costa et al., Microsoft) and [CaMeL](https://arxiv.org/abs/2503.18813) (Debenedetti et al., Google DeepMind). It is an independent project, not affiliated with Microsoft or Google.

## License

MIT
