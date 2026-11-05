using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Provenance;

/// <summary>
/// A tool wrapped by a <see cref="ProvenanceSession"/>. Before the tool runs, its policy is checked
/// against the conversation's label and the labels of any hidden content in its arguments. After it
/// runs, its result is labelled, and hidden from the model if it is untrusted.
/// </summary>
/// <remarks>
/// A blocked call never reaches the inner tool. If the enforcement code itself fails, the call is
/// refused and the tool loop is stopped: a thrown exception would otherwise become an ordinary tool
/// error and the loop would carry on (see Agent Framework ADR 0035).
/// </remarks>
public sealed class ProtectedAIFunction : DelegatingAIFunction
{
    internal enum Mode
    {
        /// <summary>Hidden-variable ids in arguments are expanded, and their labels are checked.</summary>
        Standard,

        /// <summary>Arguments are passed through unexpanded; the tool resolves variables itself and labels its result.</summary>
        Quarantine,

        /// <summary>The result is shown to the model even if untrusted, and taints the conversation.</summary>
        Reveal,
    }

    private readonly IReadOnlyList<string> _policyErrors;
    private readonly Mode _mode;
    private readonly IReadOnlyDictionary<string, object?> _properties;

    internal ProtectedAIFunction(AIFunction innerFunction, ProvenanceSession session, ToolPolicy policy, IReadOnlyList<string> policyErrors, Mode mode)
        : base(innerFunction)
    {
        Session = session;
        Policy = policy;
        _policyErrors = policyErrors;
        _mode = mode;

        var merged = new Dictionary<string, object?>(innerFunction.AdditionalProperties, StringComparer.Ordinal);
        foreach (var (key, value) in policy.ToAdditionalProperties())
        {
            merged[key] = value;
        }

        _properties = merged;
    }

    /// <summary>The session whose state this tool reads and updates.</summary>
    public ProvenanceSession Session { get; }

    /// <summary>The policy enforced on this tool.</summary>
    public ToolPolicy Policy { get; }

    /// <summary>The inner tool's metadata, with the enforced policy written over it in FIDES key format.</summary>
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _properties;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using var activity = ProvenanceTelemetry.Source.StartActivity($"provenance.check {Name}");

        Check check;
        try
        {
            check = Evaluate(arguments);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(activity, ViolationKind.EnforcementError, $"Policy evaluation failed: {ex.Message}", Session.ContextLabel, SecurityLabel.TrustedPublic, stopLoop: true);
        }

        if (check.Violations.Count > 0)
        {
            var denied = Audit(allowed: false, check.Violations, check.Reason, check.ContextLabel, check.ArgumentLabel, resultLabel: null, hiddenAs: null);
            ProvenanceTelemetry.Tag(activity, denied);
            if (Session.Options.TerminateOnViolation || check.Violations.Contains(ViolationKind.InvalidPolicy))
            {
                StopToolLoop();
            }

            return DenialText(denied);
        }

        // The tool's own exceptions propagate unchanged: the tool produced no result to label.
        // Keep FunctionInvokingChatClient.IncludeDetailedErrors off, or exception messages
        // (which may quote untrusted data) would reach the model unlabelled.
        var raw = await base.InvokeCoreAsync(check.Arguments, cancellationToken).ConfigureAwait(false);

        try
        {
            return LabelResult(activity, raw, check);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never return a result we could not label.
            return Refuse(activity, ViolationKind.EnforcementError, $"Result labelling failed: {ex.Message}", check.ContextLabel, check.ArgumentLabel, stopLoop: true);
        }
    }

    private Check Evaluate(AIFunctionArguments arguments)
    {
        var context = Session.ContextLabel;
        var violations = new List<ViolationKind>();
        var reasons = new List<string>();

        if (_policyErrors.Count > 0)
        {
            violations.Add(ViolationKind.InvalidPolicy);
            reasons.Add("the tool's policy is invalid: " + string.Join(" ", _policyErrors));
        }

        var (resolved, argumentLabel) = _mode == Mode.Standard
            ? ResolveArguments(arguments)
            : (arguments, SecurityLabel.TrustedPublic);

        var accepts = Policy.AcceptsUntrusted || Session.Options.AllowUntrustedTools.Contains(Name);
        if (!context.IsTrusted && !accepts)
        {
            violations.Add(ViolationKind.UntrustedContext);
            reasons.Add("the conversation contains untrusted content and this tool does not accept it");
        }

        if (!argumentLabel.IsTrusted && !accepts)
        {
            violations.Add(ViolationKind.UntrustedArguments);
            reasons.Add("its arguments contain untrusted hidden content and this tool does not accept it");
        }

        var effective = context.Join(argumentLabel);
        var max = Policy.MaxAllowedConfidentiality ?? Session.Options.DefaultMaxAllowedConfidentiality;
        if (max is { } cap)
        {
            if (!effective.FitsWithin(cap))
            {
                violations.Add(ViolationKind.Confidentiality);
                reasons.Add($"{LabelWire.ToWire(effective.Confidentiality)} data cannot flow to a tool that accepts at most {LabelWire.ToWire(cap)} data");
            }
            else if (effective.Confidentiality == Confidentiality.UserIdentity)
            {
                // Principal-set checks are not implemented yet, so fail closed.
                violations.Add(ViolationKind.Confidentiality);
                reasons.Add("user_identity data needs principal checks, which this version does not implement");
            }
        }

        return new Check(resolved, context, argumentLabel, violations, reasons.Count > 0 ? string.Join("; ", reasons) : null);
    }

    private (AIFunctionArguments Arguments, SecurityLabel Label) ResolveArguments(AIFunctionArguments arguments)
    {
        var labels = new List<SecurityLabel>();
        var expanded = new Dictionary<string, object?>(StringComparer.Ordinal);
        var changed = false;

        foreach (var (name, value) in arguments)
        {
            var newValue = Expand(value, labels);
            changed |= !ReferenceEquals(newValue, value);
            expanded[name] = newValue;
        }

        if (!changed)
        {
            return (arguments, SecurityLabel.TrustedPublic);
        }

        var result = new AIFunctionArguments(expanded)
        {
            Services = arguments.Services,
            Context = arguments.Context,
        };
        return (result, SecurityLabel.Combine(labels));
    }

    private object? Expand(object? value, List<SecurityLabel> labels)
    {
        switch (value)
        {
            case string text when Session.Variables.TryGet(text, out var variable):
                labels.Add(variable.Label);
                return variable.Value;

            case JsonElement { ValueKind: JsonValueKind.String } element when Session.Variables.TryGet(element.GetString(), out var variable):
                labels.Add(variable.Label);
                return variable.Value;

            case JsonElement { ValueKind: JsonValueKind.Array } array:
                List<object?>? items = null;
                var index = 0;
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Session.Variables.TryGet(item.GetString(), out var hidden))
                    {
                        items ??= [.. array.EnumerateArray().Take(index).Select(e => (object?)e.Clone())];
                        labels.Add(hidden.Label);
                        items.Add(hidden.Value);
                    }
                    else
                    {
                        items?.Add(item.Clone());
                    }

                    index++;
                }

                return items ?? value;

            case IEnumerable<string> strings:
                var list = strings.ToList();
                if (!list.Any(s => Session.Variables.TryGet(s, out _)))
                {
                    return value;
                }

                return list.Select(s =>
                {
                    if (Session.Variables.TryGet(s, out var hiddenItem))
                    {
                        labels.Add(hiddenItem.Label);
                        return hiddenItem.Value;
                    }

                    return (object?)s;
                }).ToList();

            default:
                // Nested objects are not scanned yet; a variable id inside one is passed through as text.
                return value;
        }
    }

    private object? LabelResult(System.Diagnostics.Activity? activity, object? raw, Check check)
    {
        var fallback = new SecurityLabel(
            Policy.SourceIntegrity ?? Session.Options.DefaultIntegrity,
            Policy.Confidentiality).Join(check.ArgumentLabel);

        var (value, label) = raw is LabeledValue labeled
            ? (labeled.Value, fallback.Join(labeled.Label))
            : (raw, fallback);

        if (_mode != Mode.Reveal && !label.IsTrusted && Session.Options.AutoHideUntrusted)
        {
            var variable = Session.Variables.Store(value, label, Name);

            // Hidden content never reaches the model, so it cannot taint integrity,
            // but the conversation now holds a reference to data of this sensitivity.
            Session.JoinContext(new SecurityLabel(Integrity.Trusted, label.Confidentiality));

            var hiddenEntry = Audit(allowed: true, [], null, check.ContextLabel, check.ArgumentLabel, label, variable.Id);
            ProvenanceTelemetry.Tag(activity, hiddenEntry);
            return VariableReferenceText(variable);
        }

        Session.JoinContext(label);
        var entry = Audit(allowed: true, [], null, check.ContextLabel, check.ArgumentLabel, label, hiddenAs: null);
        ProvenanceTelemetry.Tag(activity, entry);
        return value;
    }

    private string Refuse(System.Diagnostics.Activity? activity, ViolationKind kind, string reason, SecurityLabel context, SecurityLabel arguments, bool stopLoop)
    {
        var entry = Audit(allowed: false, [kind], reason, context, arguments, resultLabel: null, hiddenAs: null);
        ProvenanceTelemetry.Tag(activity, entry);
        if (stopLoop)
        {
            StopToolLoop();
        }

        return DenialText(entry);
    }

    private AuditEntry Audit(bool allowed, IReadOnlyList<ViolationKind> violations, string? reason, SecurityLabel context, SecurityLabel arguments, SecurityLabel? resultLabel, string? hiddenAs)
    {
        var entry = new AuditEntry(DateTimeOffset.UtcNow, Name, allowed, violations, reason, context, arguments, resultLabel, hiddenAs);
        Session.Record(entry);
        return entry;
    }

    private static void StopToolLoop()
    {
        if (FunctionInvokingChatClient.CurrentContext is { } invocation)
        {
            invocation.Terminate = true;
        }
    }

    private static string DenialText(AuditEntry entry) => new JsonObject
    {
        ["error"] = $"Blocked by Provenance: {entry.Reason}.",
        ["tool"] = entry.Tool,
        ["violations"] = new JsonArray([.. entry.Violations.Select(v => (JsonNode?)JsonValue.Create(v.ToString()))]),
    }.ToJsonString();

    private static string VariableReferenceText(HiddenVariable variable) => new JsonObject
    {
        ["type"] = "variable_reference",
        ["variable_id"] = variable.Id,
        [LabelWire.SecurityLabelKey] = LabelWire.ToJson(variable.Label),
        ["description"] =
            $"The output of '{variable.SourceTool}' is untrusted, so it was hidden. " +
            "Pass this variable_id to quarantined_llm to work with it, or to a tool that accepts untrusted input. " +
            "inspect_variable reveals it, but makes the conversation untrusted.",
    }.ToJsonString();

    private sealed record Check(
        AIFunctionArguments Arguments,
        SecurityLabel ContextLabel,
        SecurityLabel ArgumentLabel,
        IReadOnlyList<ViolationKind> Violations,
        string? Reason);
}
