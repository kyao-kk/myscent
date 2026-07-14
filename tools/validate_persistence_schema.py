#!/usr/bin/env python3
"""Validate structural invariants of the P0-A MySQL migration.

This is intentionally not a replacement for executing the migration against MySQL.
It prevents accidental removal of the transaction-critical tables and constraints
before the SqlSugar adapter and database integration job are added.
"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MIGRATION = ROOT / "db" / "migrations" / "0001_p0a_core.sql"


def require(condition: bool, message: str, errors: list[str]) -> None:
    if not condition:
        errors.append(message)


def normalize(sql: str) -> str:
    return re.sub(r"\s+", " ", sql.lower()).strip()


def main() -> int:
    errors: list[str] = []
    sql = MIGRATION.read_text(encoding="utf-8")
    normalized = normalize(sql)

    required_tables = {
        "myscent_schema_migration",
        "myscent_formula",
        "myscent_formula_component_current",
        "myscent_formula_event",
        "myscent_idempotency_record",
        "myscent_analysis_snapshot",
        "myscent_formula_version",
        "myscent_spec_manifest",
    }
    created_tables = set(
        re.findall(r"create table if not exists ([a-z0-9_]+)", normalized)
    )
    require(
        created_tables == required_tables,
        f"Expected tables {sorted(required_tables)}, found {sorted(created_tables)}.",
        errors,
    )

    required_fragments = [
        "unique key uq_event_formula_revision (formula_id, revision)",
        "unique key uq_event_command (command_id)",
        "primary key (actor_scope, idempotency_key)",
        "unique key uq_analysis_input",
        "before_state_hash char(64) not null",
        "after_state_hash char(64) not null",
        "aggregate_state_hash char(64) not null",
        "change_json json not null",
        "compensates_event_id varchar(36) null",
        "relative_quantity decimal(18,6) not null",
        "prediction_json json not null",
        "components_json json not null",
        "check (relative_quantity >= 0.000001)",
        "check (confidence_score >= 0 and confidence_score <= 1)",
    ]
    for fragment in required_fragments:
        require(fragment in normalized, f"Missing required SQL fragment: {fragment}", errors)

    require(
        "command_id char(36) null" in normalized,
        "formula_event.command_id must be nullable for formula_created events.",
        errors,
    )
    require(
        "command_id char(36) not null" not in extract_table(normalized, "myscent_formula_event"),
        "formula_event.command_id was made NOT NULL.",
        errors,
    )
    require(
        "foreign key (compensates_event_id) references myscent_formula_event (event_id)" in normalized,
        "Compensating undo events must reference the event they undo.",
        errors,
    )
    require(
        "on delete cascade" in extract_table(normalized, "myscent_formula_component_current"),
        "Current components must be deleted with their formula aggregate.",
        errors,
    )
    require(
        "repeat('0', 64)" in normalized,
        "Migration checksum placeholder contract is missing.",
        errors,
    )

    if errors:
        print("Persistence schema validation failed:")
        for error in errors:
            print(f" - {error}")
        return 1

    print("Persistence schema validation passed.")
    print(f" - tables: {len(required_tables)}")
    print(" - command/event/idempotency constraints: present")
    print(" - replay and analysis snapshot fields: present")
    return 0


def extract_table(sql: str, table_name: str) -> str:
    pattern = re.compile(
        rf"create table if not exists {re.escape(table_name)} \((.*?)\) engine=innodb",
        re.DOTALL,
    )
    match = pattern.search(sql)
    return match.group(1) if match else ""


if __name__ == "__main__":
    raise SystemExit(main())
