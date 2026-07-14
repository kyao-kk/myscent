#!/usr/bin/env python3
from __future__ import annotations

import json
import math
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
ATTRIBUTES = {
    "sweetness",
    "freshness",
    "warmth",
    "weight",
    "softness",
    "transparency",
    "diffusion",
    "longevity",
}
STAGES = {"top", "heart", "base"}
RELATIONS = {"synergy", "bridge", "masking", "conflict"}


class Validation:
    def __init__(self) -> None:
        self.errors: list[str] = []

    def check(self, condition: bool, message: str) -> None:
        if not condition:
            self.errors.append(message)

    def finite_range(
        self, value: Any, low: float, high: float, message: str
    ) -> None:
        self.check(
            isinstance(value, (int, float))
            and math.isfinite(float(value))
            and low <= float(value) <= high,
            message,
        )


def load_json(relative_path: str, v: Validation) -> dict[str, Any]:
    path = ROOT / relative_path
    try:
        with path.open("r", encoding="utf-8") as handle:
            data = json.load(handle)
    except FileNotFoundError:
        v.errors.append(f"{relative_path}: file not found")
        return {}
    except json.JSONDecodeError as exc:
        v.errors.append(
            f"{relative_path}: invalid JSON at line {exc.lineno}, column {exc.colno}: {exc.msg}"
        )
        return {}
    v.check(isinstance(data, dict), f"{relative_path}: root must be an object")
    return data if isinstance(data, dict) else {}


def validate_blocks(data: dict[str, Any], v: Validation) -> tuple[set[str], set[str]]:
    blocks = data.get("blocks")
    v.check(isinstance(blocks, list), "scent-blocks: blocks must be a list")
    if not isinstance(blocks, list):
        return set(), set()

    v.check(len(blocks) == 24, f"scent-blocks: expected 24 blocks, got {len(blocks)}")
    ids: list[str] = []
    groups: set[str] = set()

    for index, block in enumerate(blocks):
        prefix = f"scent-blocks.blocks[{index}]"
        v.check(isinstance(block, dict), f"{prefix}: must be an object")
        if not isinstance(block, dict):
            continue

        block_id = block.get("id")
        v.check(isinstance(block_id, str) and block_id, f"{prefix}: invalid id")
        if isinstance(block_id, str):
            ids.append(block_id)

        vector = block.get("attribute_vector")
        v.check(isinstance(vector, dict), f"{prefix}: attribute_vector must be an object")
        if isinstance(vector, dict):
            v.check(
                set(vector) == ATTRIBUTES,
                f"{prefix}: attribute keys mismatch: {sorted(vector)}",
            )
            for key, value in vector.items():
                v.finite_range(value, 0.0, 1.0, f"{prefix}.{key}: must be in 0..1")

        intensity = block.get("intensity")
        v.finite_range(
            intensity, 0.30, 1.50, f"{prefix}.intensity: must be in 0.30..1.50"
        )

        stage_weights = block.get("stage_weights")
        v.check(isinstance(stage_weights, dict), f"{prefix}: stage_weights must be object")
        if isinstance(stage_weights, dict):
            v.check(
                set(stage_weights) == STAGES,
                f"{prefix}: stage keys mismatch: {sorted(stage_weights)}",
            )
            values: list[float] = []
            for key, value in stage_weights.items():
                v.finite_range(
                    value, 0.0, 1.0, f"{prefix}.stage_weights.{key}: must be in 0..1"
                )
                if isinstance(value, (int, float)) and math.isfinite(float(value)):
                    values.append(float(value))
            if len(values) == 3:
                v.check(
                    abs(sum(values) - 1.0) <= 1e-9,
                    f"{prefix}: stage weights must sum to 1, got {sum(values)}",
                )

        v.finite_range(
            block.get("data_quality"),
            0.0,
            1.0,
            f"{prefix}.data_quality: must be in 0..1",
        )

        overload_groups = block.get("overload_groups")
        v.check(
            isinstance(overload_groups, list),
            f"{prefix}.overload_groups: must be a list",
        )
        if isinstance(overload_groups, list):
            for group in overload_groups:
                v.check(isinstance(group, str) and group, f"{prefix}: invalid group")
                if isinstance(group, str):
                    groups.add(group)

    v.check(len(ids) == len(set(ids)), "scent-blocks: duplicate block ids")
    return set(ids), groups


def validate_relations(
    data: dict[str, Any], block_ids: set[str], block_groups: set[str], v: Validation
) -> set[str]:
    pair_rules = data.get("pair_rules")
    group_rules = data.get("group_rules")
    v.check(isinstance(pair_rules, list), "scent-relations: pair_rules must be list")
    v.check(isinstance(group_rules, list), "scent-relations: group_rules must be list")
    rule_ids: list[str] = []

    if isinstance(pair_rules, list):
        for index, rule in enumerate(pair_rules):
            prefix = f"scent-relations.pair_rules[{index}]"
            v.check(isinstance(rule, dict), f"{prefix}: must be object")
            if not isinstance(rule, dict):
                continue
            rule_id = rule.get("rule_id")
            v.check(isinstance(rule_id, str) and rule_id, f"{prefix}: invalid rule_id")
            if isinstance(rule_id, str):
                rule_ids.append(rule_id)
            v.check(rule.get("source") in block_ids, f"{prefix}: unknown source")
            v.check(rule.get("target") in block_ids, f"{prefix}: unknown target")
            v.check(rule.get("relation") in RELATIONS, f"{prefix}: invalid relation")
            v.finite_range(
                rule.get("strength"), 0.0, 1.5, f"{prefix}.strength: invalid"
            )
            v.finite_range(
                rule.get("threshold"), 0.0, 1.0, f"{prefix}.threshold: invalid"
            )
            affected = rule.get("affected_metrics")
            v.check(isinstance(affected, list) and affected, f"{prefix}: no metrics")

    if isinstance(group_rules, list):
        for index, rule in enumerate(group_rules):
            prefix = f"scent-relations.group_rules[{index}]"
            v.check(isinstance(rule, dict), f"{prefix}: must be object")
            if not isinstance(rule, dict):
                continue
            rule_id = rule.get("rule_id")
            v.check(isinstance(rule_id, str) and rule_id, f"{prefix}: invalid rule_id")
            if isinstance(rule_id, str):
                rule_ids.append(rule_id)
            v.check(
                rule.get("group") in block_groups,
                f"{prefix}: group {rule.get('group')!r} is unused by blocks",
            )
            v.check(
                isinstance(rule.get("min_active_components"), int)
                and rule["min_active_components"] >= 1,
                f"{prefix}: invalid min_active_components",
            )
            v.finite_range(
                rule.get("combined_effective_share_threshold"),
                0.0,
                1.0,
                f"{prefix}: invalid share threshold",
            )

    v.check(len(rule_ids) == len(set(rule_ids)), "scent-relations: duplicate rule ids")
    return set(rule_ids)


def iter_block_references(value: Any) -> list[str]:
    refs: list[str] = []
    if isinstance(value, dict):
        for key, item in value.items():
            if key in {
                "block_id",
                "from_block_id",
                "to_block_id",
                "source",
                "target",
            } and isinstance(item, str) and item.startswith("SB-"):
                refs.append(item)
            else:
                refs.extend(iter_block_references(item))
    elif isinstance(value, list):
        for item in value:
            refs.extend(iter_block_references(item))
    return refs


def validate_engine(data: dict[str, Any], v: Validation) -> None:
    v.check(data.get("engine_version") == "0.1.0", "scent-engine: engine_version must be 0.1.0")
    deps = data.get("dependencies")
    v.check(isinstance(deps, dict), "scent-engine: dependencies must be object")
    if isinstance(deps, dict):
        v.check(
            deps.get("block_library") == "scent-blocks.v0.1.json",
            "scent-engine: block library dependency mismatch",
        )
        v.check(
            deps.get("relation_library") == "scent-relations.v0.1.json",
            "scent-engine: relation library dependency mismatch",
        )
    keys = data.get("attributes", {}).get("keys") if isinstance(data.get("attributes"), dict) else None
    v.check(isinstance(keys, list) and set(keys) == ATTRIBUTES, "scent-engine: attribute keys mismatch")
    hash_fields = data.get("canonicalization", {}).get("formula_hash_fields") if isinstance(data.get("canonicalization"), dict) else None
    v.check(isinstance(hash_fields, list) and "components" in hash_fields, "scent-engine: components missing from formula hash")
    exclusions = data.get("canonicalization", {}).get("exclude_from_hash") if isinstance(data.get("canonicalization"), dict) else None
    v.check(isinstance(exclusions, list) and "scene_id" in exclusions, "scent-engine: scene_id must be excluded from formula hash")


def validate_fixtures(data: dict[str, Any], block_ids: set[str], v: Validation) -> None:
    fixtures = data.get("fixtures")
    v.check(isinstance(fixtures, list), "scent-fixtures: fixtures must be list")
    if not isinstance(fixtures, list):
        return
    v.check(len(fixtures) == 15, f"scent-fixtures: expected 15 fixtures, got {len(fixtures)}")
    ids = [fixture.get("id") for fixture in fixtures if isinstance(fixture, dict)]
    v.check(len(ids) == len(set(ids)), "scent-fixtures: duplicate fixture ids")
    fixture_ids = {item for item in ids if isinstance(item, str)}

    for index, fixture in enumerate(fixtures):
        prefix = f"scent-fixtures.fixtures[{index}]"
        v.check(isinstance(fixture, dict), f"{prefix}: must be object")
        if not isinstance(fixture, dict):
            continue
        for ref in iter_block_references(fixture):
            v.check(ref in block_ids, f"{prefix}: unknown block reference {ref}")
        for assertion in fixture.get("assertions", []):
            if not isinstance(assertion, dict):
                continue
            ref = assertion.get("right_fixture")
            if isinstance(ref, str):
                target_id = ref.split(".", 1)[0]
                v.check(
                    target_id in fixture_ids,
                    f"{prefix}: unknown fixture reference {target_id}",
                )


def validate_protocol_and_machine(
    protocol: dict[str, Any], machine: dict[str, Any], v: Validation
) -> None:
    commands = protocol.get("commands")
    v.check(isinstance(commands, list), "command-protocol: commands must be list")
    command_types: set[str] = set()
    if isinstance(commands, list):
        all_types = [
            item.get("type")
            for item in commands
            if isinstance(item, dict) and isinstance(item.get("type"), str)
        ]
        command_types = set(all_types)
        v.check(len(all_types) == len(command_types), "command-protocol: duplicate command types")

    regions = machine.get("regions")
    v.check(isinstance(regions, dict), "state-machine: regions must be object")
    if not isinstance(regions, dict):
        return

    for region_name, region in regions.items():
        prefix = f"state-machine.regions.{region_name}"
        v.check(isinstance(region, dict), f"{prefix}: must be object")
        if not isinstance(region, dict):
            continue
        states = region.get("states")
        v.check(isinstance(states, dict) and states, f"{prefix}: states missing")
        if not isinstance(states, dict):
            continue
        v.check(region.get("initial") in states, f"{prefix}: initial state missing")
        for state_name, state in states.items():
            if not isinstance(state, dict):
                v.errors.append(f"{prefix}.states.{state_name}: must be object")
                continue
            allowed = state.get("allowed_commands", [])
            v.check(isinstance(allowed, list), f"{prefix}.{state_name}: allowed_commands must be list")
            if isinstance(allowed, list):
                for command in allowed:
                    v.check(
                        command in command_types,
                        f"{prefix}.{state_name}: unknown command {command}",
                    )

    transitions = machine.get("transitions")
    v.check(isinstance(transitions, list), "state-machine: transitions must be list")
    if isinstance(transitions, list):
        for index, transition in enumerate(transitions):
            prefix = f"state-machine.transitions[{index}]"
            v.check(isinstance(transition, dict), f"{prefix}: must be object")
            if not isinstance(transition, dict):
                continue
            region_name = transition.get("region")
            v.check(region_name in regions, f"{prefix}: unknown region {region_name}")
            if region_name not in regions or not isinstance(regions[region_name], dict):
                continue
            states = regions[region_name].get("states", {})
            from_states = transition.get("from")
            v.check(isinstance(from_states, list), f"{prefix}: from must be list")
            if isinstance(from_states, list):
                for state in from_states:
                    v.check(state in states, f"{prefix}: unknown from state {state}")
            to_state = transition.get("to")
            if to_state != "unchanged":
                v.check(to_state in states, f"{prefix}: unknown to state {to_state}")


def main() -> int:
    v = Validation()
    blocks = load_json("specs/scent/scent-blocks.v0.1.json", v)
    relations = load_json("specs/scent/scent-relations.v0.1.json", v)
    engine = load_json("specs/scent/scent-engine.v0.1.json", v)
    fixtures = load_json("specs/scent/scent-fixtures.v0.1.json", v)
    protocol = load_json("specs/workbench/formula-command-protocol.v0.1.json", v)
    machine = load_json("specs/workbench/workbench-state-machine.v0.1.json", v)

    block_ids, block_groups = validate_blocks(blocks, v)
    validate_relations(relations, block_ids, block_groups, v)
    validate_engine(engine, v)
    validate_fixtures(fixtures, block_ids, v)
    validate_protocol_and_machine(protocol, machine, v)

    if v.errors:
        print(f"Specification validation failed with {len(v.errors)} error(s):")
        for error in v.errors:
            print(f" - {error}")
        return 1

    print("Specification validation passed.")
    print(f" - scent blocks: {len(block_ids)}")
    print(f" - fixtures: {len(fixtures.get('fixtures', []))}")
    print(f" - commands: {len(protocol.get('commands', []))}")
    print(f" - state regions: {len(machine.get('regions', {}))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
