# Provenance

Deterministic prompt-injection defence for .NET agents built on [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai).

An agent that reads untrusted content (issues, emails, web pages) and can also act (post, send, write) can be hijacked by instructions hidden in that content. Better prompts don't fix this reliably. Provenance enforces it in code instead: every piece of content carries a label, and a tool can only run if the labels in play allow it.

> **Status: pre-alpha.** The API will change. Not yet benchmarked.

## What it stops

| Attack | What happens |
| --- | --- |
| Injected instructions in a tool result | Untrusted results are hidden from the model behind a `var_…` reference, so the planner never reads them |
| The model reveals the content anyway | The conversation becomes untrusted, and every tool that doesn't accept untrusted input is blocked from then on |
| Hidden content passed into a tool argument | The argument carries the untrusted label, and the call is blocked |
| Private data sent to a public destination | Blocked by the destination's confidentiality cap |
| An unwrapped tool slipped into the request | The whole request is refused before the model is called |

## Quick start

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

Every decision is recorded in `session.AuditLog` and as an OpenTelemetry span; subscribe with `.AddSource("Provenance")`.

## Limitations (today)

- Only top-level arguments and string arrays are scanned for hidden-variable ids; nested objects are not.
- `user_identity` data is blocked at any capped sink, because principal checks are not implemented yet.
- Approvals, per-item labels and persistence across process restarts are not implemented.
- Streaming works, but the guard checks only the tools, not content mid-stream.
- Keep `FunctionInvokingChatClient.IncludeDetailedErrors` off: tool exception messages are not labelled.

## Roadmap

1. AgentDojo benchmark: utility and attack-success rates for each model, with and without Provenance.
2. MCP client integration: annotation-derived policies and `_meta.ifc` labels applied automatically.
3. Microsoft Agent Framework adapter (`AIAgentBuilder`).
4. Human approval for policy violations, bound to the exact call.

## Credits

The design follows [FIDES](https://arxiv.org/abs/2505.23643) (Costa et al., Microsoft) and [CaMeL](https://arxiv.org/abs/2503.18813) (Debenedetti et al., Google DeepMind). It is an independent project, not affiliated with Microsoft or Google.

## License

MIT
