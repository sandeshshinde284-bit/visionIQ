"""Run a hotspot sweep now and print the report: ``python -m app.hotspots.cli``.

This is the on-demand path, and the one a scheduler calls (for example a Cloud Run job started
by Cloud Scheduler). The service can also run sweeps itself on an interval
(HOTSPOT_SWEEP_INTERVAL_HOURS). Exit code 0 when a sweep was stored, 3 when another sweep was
already running.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys

import structlog

from app.config import Settings, get_settings
from app.db import connect
from app.hotspots.sweep import SweepResult, Trigger, report, run_sweep
from app.logging_config import configure_from_environment


def sweep_now(settings: Settings, trigger: Trigger) -> SweepResult | None:
    with connect(settings) as connection:
        return run_sweep(
            connection, trigger, settings.hotspot_window_days, settings.hotspot_area_depth
        )


def _text(summary: dict) -> str:
    lines = [
        f"Hotspot sweep {summary['sweep_id']} ({summary['trigger']}, last "
        f"{summary['window_days']} days, {summary['scoring_version']})",
        f"{summary['areas_scored']} areas scored, {summary['areas_flagged']} flagged",
        "",
        f"{'#':>3}  {'band':<17} {'lift':>6} {'fixes':>5} {'reverted':>8} {'changes':>7}  area",
    ]
    for area in summary["areas"]:
        lines.append(
            f"{area['rank']:>3}  {area['band']:<17} {area['lift']:>6.2f} {area['defect_fixes']:>5} "
            f"{area['reverted_fixes']:>8} {area['changes']:>7}  "
            f"{area['project']}/{area['repository']}: {area['area']}"
        )
        for fix in area["evidence"][:5]:
            kind = "PR" if fix["fix_kind"] == "pull_request" else "commit"
            reverted = " (reverted)" if fix.get("reverted") else ""
            lines.append(f"       ticket {fix['ticket_id']} -> {kind} {fix['fix_id']}{reverted}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run a VisionIQ hotspot sweep and print it.")
    parser.add_argument("--window-days", type=int, help="look back this many days")
    parser.add_argument("--depth", type=int, help="directory depth that defines an area")
    parser.add_argument("--limit", type=int, default=20, help="areas to print (default 20)")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    parser.add_argument(
        "--scheduled", action="store_true", help="record the sweep as started by a schedule"
    )
    args = parser.parse_args(argv)
    configure_from_environment()
    settings = get_settings()
    if args.window_days is not None or args.depth is not None:
        settings = settings.model_copy(
            update={
                "hotspot_window_days": args.window_days or settings.hotspot_window_days,
                "hotspot_area_depth": args.depth or settings.hotspot_area_depth,
            }
        )
    result = sweep_now(settings, "schedule" if args.scheduled else "on_demand")
    if result is None:
        sys.stderr.write("Another hotspot sweep is running; nothing was stored.\n")
        return 3
    summary = report(result, args.limit)
    sys.stdout.write((json.dumps(summary, indent=2) if args.json else _text(summary)) + "\n")
    return 0


async def run_scheduled_sweeps(settings: Settings) -> None:
    """In-process schedule: one sweep shortly after start-up, then every interval."""
    log = structlog.get_logger()
    interval = settings.hotspot_sweep_interval_hours * 3600
    log.info("hotspot_schedule_started", interval_hours=settings.hotspot_sweep_interval_hours)
    await asyncio.sleep(min(60.0, interval))
    while True:
        try:
            await asyncio.to_thread(sweep_now, settings, "schedule")
        except Exception as error:
            log.error("hotspot_sweep_failed", error_type=type(error).__name__)
        await asyncio.sleep(interval)


if __name__ == "__main__":
    sys.exit(main())
