"""Runs AgentDojo against an agent host, with and without Provenance, and reports three numbers:

- utility: share of user tasks solved with no attack
- utility under attack: share of (user task, injection) pairs where the user task was still solved
- attack success rate: share of pairs where the attacker's goal was achieved

Example:
    python -m provenance_agentdojo.run --host http://localhost:5080 --suite workspace \
        --user-tasks user_task_0 user_task_1 --injection-tasks injection_task_0

With --resume, tasks already in results/traces are kept and only the rest are run, so a run cut
short by a daily rate limit can be continued the next day with the same command.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import httpx
from agentdojo.attacks.attack_registry import load_attack
from agentdojo.benchmark import benchmark_suite_with_injections, benchmark_suite_without_injections
from agentdojo.logging import Logger
from agentdojo.task_suite.load_suites import get_suite

from .pipeline import HostPipeline
from .tool_server import ToolServer

BENCHMARK_VERSION = "v1.2.2"


class _QuietLogger(Logger):
    """Keeps AgentDojo from printing every chat message; per-task traces are still written to `logdir`."""

    def __init__(self, logdir: str) -> None:
        self.logdir = logdir
        self.messages: list = []

    def log(self, *args, **kwargs) -> None:
        pass

    def log_error(self, *args, **kwargs) -> None:
        pass


def _rate(results: dict) -> float | None:
    values = list(results.values())
    return round(sum(values) / len(values), 4) if values else None


HOST_ERROR_PREFIX = "[host error]"


def _is_failed_trace(path: Path) -> bool:
    """A trace whose final answer is a host error: the task never really ran."""
    try:
        messages = json.loads(path.read_text(encoding="utf-8")).get("messages") or []
        content = (messages[-1].get("content") or [{}])[0].get("content") or ""
    except (OSError, ValueError, IndexError, AttributeError):
        return True  # unreadable or half-written: treat as not done
    return content.startswith(HOST_ERROR_PREFIX)


def _failed_tasks(pipeline_dir: Path) -> set[tuple[str, str]]:
    """(user task, injection task or "") for every trace under a pipeline that failed in the host."""
    failed = set()
    for path in pipeline_dir.glob("*/*/*.json"):
        if _is_failed_trace(path):
            user_task, attack = path.parent.parent.name, path.parent.name
            failed.add((user_task, "" if attack == "none" else path.stem))
    return failed


def _drop_failed_traces(pipeline_dir: Path) -> int:
    """Delete traces of tasks that failed in the host, so --resume runs them again."""
    dropped = 0
    for path in pipeline_dir.glob("*/*/*.json"):
        if _is_failed_trace(path):
            path.unlink()
            dropped += 1
    return dropped


def run(args: argparse.Namespace) -> dict:
    suite = get_suite(BENCHMARK_VERSION, args.suite)
    report: dict = {
        "benchmark_version": BENCHMARK_VERSION,
        "suite": args.suite,
        "attack": args.attack,
        "model_label": args.model_label,
        "user_tasks": args.user_tasks or "all",
        "injection_tasks": args.injection_tasks or "all",
        "resumed": args.resume,
        "runs": {},
    }

    # baseline: no Provenance. defended: strict Provenance. typed: Provenance with typed quarantine answers.
    variants = {
        "baseline": ["baseline"],
        "defended": ["defended"],
        "typed": ["typed"],
        "both": ["baseline", "defended"],
        "all": ["baseline", "defended", "typed"],
    }[args.mode]
    traces_dir = Path(args.out) / "traces"
    with ToolServer() as tool_server, _QuietLogger(str(traces_dir)):
        for label in variants:
            pipeline = HostPipeline(
                tool_server, args.host, args.suite, defended=label != "baseline",
                model_label=args.model_label, wait_for_rate_limits=args.wait, typed_answers=label == "typed",
            )
            pipeline_dir = traces_dir / pipeline.name / suite.name
            if args.resume:
                dropped = _drop_failed_traces(pipeline_dir)
                if dropped:
                    print(f"{label}: retrying {dropped} task(s) that failed in the host last time")
            started = time.monotonic()

            no_attack = benchmark_suite_without_injections(
                pipeline,
                suite,
                logdir=traces_dir,
                force_rerun=not args.resume,
                user_tasks=args.user_tasks,
                benchmark_version=BENCHMARK_VERSION,
            )

            attack = load_attack(args.attack, suite, pipeline)
            with_attack = benchmark_suite_with_injections(
                pipeline,
                suite,
                attack,
                logdir=traces_dir,
                force_rerun=not args.resume,
                user_tasks=args.user_tasks,
                injection_tasks=args.injection_tasks,
                verbose=False,
                benchmark_version=BENCHMARK_VERSION,
            )

            # Tasks that failed in the host (rate limit, network) never ran: leave them out of the
            # rates rather than counting them as failures, and report how many are still to do.
            failed = _failed_tasks(pipeline_dir)
            utility = {k: v for k, v in no_attack["utility_results"].items() if k not in failed}
            utility_attacked = {k: v for k, v in with_attack["utility_results"].items() if k not in failed}
            security = {k: v for k, v in with_attack["security_results"].items() if k not in failed}
            to_do = len(no_attack["utility_results"]) + len(with_attack["security_results"]) - len(utility) - len(security)

            report["runs"][label] = {
                "utility": _rate(utility),
                "utility_under_attack": _rate(utility_attacked),
                "attack_success_rate": _rate(security),
                "user_tasks_run": len(utility),
                "attack_pairs_run": len(security),
                "still_to_run": to_do,
                "seconds": round(time.monotonic() - started, 1),
                "tasks_sent_to_host": pipeline.tasks_run,
                "host_errors": len(pipeline.host_errors),
                "first_host_error": pipeline.host_errors[0] if pipeline.host_errors else None,
                "per_pair": {
                    f"{u}|{i}": {"utility": with_attack["utility_results"][(u, i)], "attack_succeeded": s}
                    for (u, i), s in security.items()
                },
            }

    return report


def _print(report: dict) -> None:
    print(f"\nAgentDojo {report['benchmark_version']} · suite {report['suite']} · attack {report['attack']}")
    print(f"{'':10} {'utility':>8} {'utility under attack':>21} {'attack success':>15} {'pairs':>6} {'host errors':>12} {'still to run':>13}")
    for label, r in report["runs"].items():
        fmt = lambda v: "n/a" if v is None else f"{v:.0%}"  # noqa: E731
        print(
            f"{label:10} {fmt(r['utility']):>8} {fmt(r['utility_under_attack']):>21} "
            f"{fmt(r['attack_success_rate']):>15} {r['attack_pairs_run']:>6} {r['host_errors']:>12} {r['still_to_run']:>13}"
        )
    for label, r in report["runs"].items():
        if r["first_host_error"]:
            print(f"\n{label}: {r['host_errors']} of {r['tasks_sent_to_host']} tasks failed in the host. First error:\n  {r['first_host_error']}")
    if any(r["still_to_run"] for r in report["runs"].values()):
        print("\nSome tasks didn't run. Add --resume to the same command later to run only those.")


def _check_host(host: str) -> None:
    """Fail with a plain message, not a long traceback, when the .NET host isn't running."""
    try:
        httpx.get(host, timeout=5)
    except httpx.HTTPError:
        sys.exit(
            f"Can't reach the agent host at {host}.\n"
            "Start it first in another window and wait until it says it's listening:\n"
            "  dotnet run --project benchmarks/agentdojo/host -- --Model:Name=<model>"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="http://localhost:5080", help="URL of the agent host")
    parser.add_argument("--suite", default="workspace", choices=["workspace", "travel", "banking", "slack"])
    parser.add_argument("--mode", default="both", choices=["baseline", "defended", "typed", "both", "all"],
        help="baseline: no Provenance; defended: strict Provenance; typed: Provenance with typed quarantine answers; "
        "both: baseline and defended; all: all three",
    )
    parser.add_argument("--attack", default="important_instructions")
    parser.add_argument("--user-tasks", nargs="*", default=None, help="Subset of user task ids (default: all)")
    parser.add_argument("--injection-tasks", nargs="*", default=None, help="Subset of injection task ids (default: all)")
    parser.add_argument(
        "--model-label",
        default="local",
        help="Name AgentDojo's attacks use to address the model; must be a key AgentDojo knows (default: local)",
    )
    parser.add_argument("--out", default="results", help="Directory for the JSON report and traces")
    parser.add_argument(
        "--resume",
        action="store_true",
        help="Keep tasks already in <out>/traces and run only the rest (use a separate --out per model)",
    )
    parser.add_argument(
        "--wait",
        action="store_true",
        help="Wait out the model's rate limits, even a daily cap, so the run finishes unattended",
    )
    args = parser.parse_args()

    _check_host(args.host)
    if args.wait:
        print("Waiting through rate limits: this can take a day or more. Keep both windows open and the PC awake.")
    report = run(args)
    _print(report)

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"{args.suite}-{args.attack}-{time.strftime('%Y%m%d-%H%M%S')}.json"
    path.write_text(json.dumps(report, indent=2))
    print(f"\nReport written to {path}")


if __name__ == "__main__":
    main()
