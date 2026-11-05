"""Runs AgentDojo against an agent host, with and without Provenance, and reports three numbers:

- utility: share of user tasks solved with no attack
- utility under attack: share of (user task, injection) pairs where the user task was still solved
- attack success rate: share of pairs where the attacker's goal was achieved

Example:
    python -m provenance_agentdojo.run --host http://localhost:5080 --suite workspace \
        --user-tasks user_task_0 user_task_1 --injection-tasks injection_task_0
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

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


def run(args: argparse.Namespace) -> dict:
    suite = get_suite(BENCHMARK_VERSION, args.suite)
    report: dict = {
        "benchmark_version": BENCHMARK_VERSION,
        "suite": args.suite,
        "attack": args.attack,
        "model_label": args.model_label,
        "user_tasks": args.user_tasks or "all",
        "injection_tasks": args.injection_tasks or "all",
        "runs": {},
    }

    modes = {"baseline": [False], "defended": [True], "both": [False, True]}[args.mode]
    traces_dir = Path(args.out) / "traces"
    with ToolServer() as tool_server, _QuietLogger(str(traces_dir)):
        for defended in modes:
            pipeline = HostPipeline(tool_server, args.host, args.suite, defended, model_label=args.model_label)
            label = "defended" if defended else "baseline"
            started = time.monotonic()

            no_attack = benchmark_suite_without_injections(
                pipeline,
                suite,
                logdir=None,
                force_rerun=True,
                user_tasks=args.user_tasks,
                benchmark_version=BENCHMARK_VERSION,
            )

            attack = load_attack(args.attack, suite, pipeline)
            with_attack = benchmark_suite_with_injections(
                pipeline,
                suite,
                attack,
                logdir=None,
                force_rerun=True,
                user_tasks=args.user_tasks,
                injection_tasks=args.injection_tasks,
                verbose=False,
                benchmark_version=BENCHMARK_VERSION,
            )

            report["runs"][label] = {
                "utility": _rate(no_attack["utility_results"]),
                "utility_under_attack": _rate(with_attack["utility_results"]),
                "attack_success_rate": _rate(with_attack["security_results"]),
                "user_tasks_run": len(no_attack["utility_results"]),
                "attack_pairs_run": len(with_attack["security_results"]),
                "seconds": round(time.monotonic() - started, 1),
                "tasks_sent_to_host": pipeline.tasks_run,
                "host_errors": len(pipeline.host_errors),
                "first_host_error": pipeline.host_errors[0] if pipeline.host_errors else None,
                "per_pair": {
                    f"{u}|{i}": {"utility": with_attack["utility_results"][(u, i)], "attack_succeeded": s}
                    for (u, i), s in with_attack["security_results"].items()
                },
            }

    return report


def _print(report: dict) -> None:
    print(f"\nAgentDojo {report['benchmark_version']} · suite {report['suite']} · attack {report['attack']}")
    print(f"{'':10} {'utility':>8} {'utility under attack':>21} {'attack success':>15} {'pairs':>6} {'host errors':>12}")
    for label, r in report["runs"].items():
        fmt = lambda v: "n/a" if v is None else f"{v:.0%}"  # noqa: E731
        print(
            f"{label:10} {fmt(r['utility']):>8} {fmt(r['utility_under_attack']):>21} "
            f"{fmt(r['attack_success_rate']):>15} {r['attack_pairs_run']:>6} {r['host_errors']:>12}"
        )
    for label, r in report["runs"].items():
        if r["first_host_error"]:
            print(f"\n{label}: {r['host_errors']} of {r['tasks_sent_to_host']} tasks failed in the host. First error:\n  {r['first_host_error']}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="http://localhost:5080", help="URL of the agent host")
    parser.add_argument("--suite", default="workspace", choices=["workspace", "travel", "banking", "slack"])
    parser.add_argument("--mode", default="both", choices=["baseline", "defended", "both"])
    parser.add_argument("--attack", default="important_instructions")
    parser.add_argument("--user-tasks", nargs="*", default=None, help="Subset of user task ids (default: all)")
    parser.add_argument("--injection-tasks", nargs="*", default=None, help="Subset of injection task ids (default: all)")
    parser.add_argument(
        "--model-label",
        default="local",
        help="Name AgentDojo's attacks use to address the model; must be a key AgentDojo knows (default: local)",
    )
    parser.add_argument("--out", default="results", help="Directory for the JSON report")
    args = parser.parse_args()

    report = run(args)
    _print(report)

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"{args.suite}-{args.attack}-{time.strftime('%Y%m%d-%H%M%S')}.json"
    path.write_text(json.dumps(report, indent=2))
    print(f"\nReport written to {path}")


if __name__ == "__main__":
    main()
