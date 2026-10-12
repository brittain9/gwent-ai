"""Answer kinds, exposure tags and revive reach from the client's ability graphs.

Imported by `card_roles.py`, which writes the results into `data/cards/roles.json`
(fields `answer_kinds`, `exposure`, `revive_reach`, `replayable_by`, and the
top-level `weather_tokens`). Same method as the roles: the structure of the
ability graphs (node types, enum parameters, constants in ports and variable
declarations, data-flow connections). No card text, no card stats: the
numeric parameters (amounts, thresholds, counts) are split off into the
untracked `.local/cards/answer-params/<roles sha>.json` (`split`, `join`; run
`scripts/answer_kinds.py` to write it from the user's client).

The engine rules each parameter stands for, from the engine's behaviour:

- `FilterPowerLevelNode.PowerFilter`: 0 Highest = exactly `Num` cards (ties
  broken by board order), 1 HighestLevel = every card on the top `Num` power
  levels (all tied cards), 2 Lowest, 3 LowestLevel. Units waiting in Ambush
  are never candidates.
- `ChangePowerNode.Operation`: 0 add, 1 remove (damage), 2 set, 3 multiply
  (percent), 4 restore; `TargetedPower`: 0 current, 1 base, 2 permanent.
- `ResetCardsNode.ResetMode` / `TransformCardsNode.ResetMode`: bits of EResetMode.
- `ChangeTokensNode` with the Lock token (4); `ChangeLocationTokensNode` adds row
  tokens (ELocationToken) whose own abilities (`LocationTokenAbility`) hit units.
"""

from collections import Counter

import hashlib
import json
from pathlib import Path

import clientdata

from card_roles import (
    ACTIVE,
    ACTOR_PORTS,
    DECK,
    GRAVEYARD,
    HAND,
    NEUTRAL,
    OPP,
    OWN,
    SELF,
    UNKNOWN,
    UNIT_TYPE,
    SPAWN_POOL,
    facts_of,
)

POWER_FILTER = {
    0: ("highest", "one"),
    1: ("highest", "all"),
    2: ("lowest", "one"),
    3: ("lowest", "all"),
}
POWER_OP = {0: "add", 1: "remove", 2: "set", 3: "multiply", 4: "restore"}
POWER_TYPE = {0: "current", 1: "base", 2: "permanent", 3: "armor"}
NEGATED = {"==": "!=", "!=": "==", "<": ">=", ">": "<=", "<=": ">", ">=": "<"}
COMPARE = {0: "==", 1: "!=", 2: "<", 3: ">", 4: "<=", 5: ">="}
POWER_STATE = {
    0: "normal",
    1: "wounded",
    2: "boosted",
    3: "weakened",
    4: "strengthened",
}
RESET_BITS = (
    (1, "power"),
    (2, "promotion"),
    (4, "leader_used"),
    (8, "tokens"),
    (0x10, "template_power"),
    (0x20, "timer"),
    (0x40, "armor"),
    (0x80, "categories"),
)
CARD_TOKENS = (
    (1, "resilience"),
    (4, "lock"),
    (8, "ambush"),
    (0x10, "immune_to_weather"),
    (0x100, "not_targetable"),
)
LOCATION_TOKENS = (
    (1, "frost"),
    (2, "fog"),
    (4, "rain"),
    (8, "fire"),
    (0x10, "drought"),
    (0x20, "ragh_nar_roog"),
    (0x40, "skellige_storm"),
    (0x80, "golden_froth"),
    (0x100, "moonlight"),
    (0x200, "pitfall_trap"),
    (0x400, "dragons_dream"),
    (0x800, "blood_moon"),
    (0x1000, "fire_sp"),
)
TIERS = ((1, "leader"), (2, "bronze"), (4, "silver"), (8, "gold"))
TYPES = ((2, "special"), (4, "unit"))
HARM = {"damage", "destroy", "consume", "banish", "lock", "reset", "transform", "set", "multiply"}
RANDOM = {
    "CardListGetRandomElementNode",
    "CardListShuffleNode",
    "IntGetRandomNode",
    "GetRandomLocationTokenNode",
}
ROW_READS = {
    "RequestLocationTargetsNode",
    "GetLocationCards",
    "LocationListGetCardsNode",
    "BreakLocationNode",
}
# Default port values of the engine's node classes, for ports the XML leaves out.
DEFAULT_IGNORE = 8 | 0x100  # GetCardsNode.Ignore: Ambush | NotTargetable


def bits(mask, names):
    return [name for bit, name in names if mask is not None and mask & bit]


def literal(graph, node, name, default=None):
    value = graph.int_value(node.find(name)) if node.find(name) is not None else None
    return default if value is None else value


def upstream_nodes(graph, port_id):
    """Nodes whose data reaches a port, nearest first: [(node, the port read)].
    Variables are resolved; who acts (Attacker, Requester...) is not followed."""
    seen, order, queue = set(), [], [port_id]
    while queue:
        pid = queue.pop(0)
        for s in graph.upstream(pid):
            node, port = graph.port[s]
            if node.get("Id") in seen:
                continue
            seen.add(node.get("Id"))
            order.append((node, port.tag))
            for child in node:
                for p in [child, *child]:
                    if (
                        p.get("Id")
                        and p.get("Type") != "FlowConnector"
                        and p.tag not in ACTOR_PORTS
                    ):
                        queue.append(p.get("Id"))
    return order


def row_threshold(graph):
    """A row-total condition: (op, value) of an IntCompareNode that reads a
    row's score against a constant (Geralt: Igni), else None."""
    for node in graph.of_type("IntCompareNode", "IntBranchNode"):
        types = {d[1] for d in graph.data_deps(node) if d[0] == "type"}
        if "GetLocationScore" not in types:
            continue
        a, b = node.find("A"), node.find("B")
        value = graph.int_value(b)
        flip = False
        if value is None:
            value, flip = graph.int_value(a), True
        if value is None:
            continue
        op = COMPARE.get(literal(graph, node, "Op", 0), "?")
        if flip:
            op = {"<": ">", ">": "<", "<=": ">=", ">=": "<="}.get(op, op)
        return op, value
    return None


def selector(graph, port_id):
    """How an effect's targets are chosen, from the nodes upstream of its target
    port: select (chosen, highest, lowest, random, row, adjacent, event, all),
    ties, count, filters and where the candidates are."""
    nodes = upstream_nodes(graph, port_id)
    types = Counter(n.get("Type") for n, _ in nodes)
    out = {}
    level = next((n for n, _ in nodes if n.get("Type") == "FilterPowerLevelNode"), None)
    chosen = next(
        (n for n, _ in nodes if n.get("Type") == "RequestCardTargetsNode"), None
    )
    shape = any(
        n.get("Type") == "RequestCardTargetsNode" and tag == "TargetsInShape"
        for n, tag in nodes
    )
    if level is not None:
        select, ties = POWER_FILTER.get(
            literal(graph, level, "PowerFilter", 1), ("?", "?")
        )
        out.update(select=select, ties=ties, levels=literal(graph, level, "Num", 1))
        if set(types) & RANDOM:
            out["pick"] = "random_among"
    elif chosen is not None:
        out["select"] = "adjacent" if shape else "chosen"
        out["count"] = literal(graph, chosen, "MaxTargets", 1)
    elif set(types) & RANDOM:
        out["select"] = "random"
    elif types["CardListGetRangeNode"]:
        rng = next(n for n, _ in nodes if n.get("Type") == "CardListGetRangeNode")
        out["select"] = "first_n"  # the first N of an ordered list
        out["count"] = literal(graph, rng, "Length", None)
    elif types["GetCardsInShapeNode"]:
        out["select"] = "adjacent"
    elif set(types) & ROW_READS:
        out["select"] = "row"
    elif any(n.get("Type").endswith("Trigger") for n, _ in nodes):
        out["select"] = "event"  # the card a trigger hands over
    elif types["GetAbilityOwnerCardNode"] and not types["GetCardsNode"]:
        out["select"] = "self"
    else:
        out["select"] = "all"
    # One row's units: a row picked by the player or read by location.
    if set(types) & {
        "RequestLocationTargetsNode",
        "GetLocationCards",
        "BreakLocationNode",
    }:
        out["row"] = True
    tiers, ignore, locations = 15, 0, 0
    for n, _ in nodes:
        kind = n.get("Type")
        if kind == "GetCardsNode":
            tiers &= literal(graph, n, "CardTiers", 15)
            ignore |= literal(graph, n, "Ignore", DEFAULT_IGNORE)
            loc = literal(graph, n, "LocationId", None)
            locations |= ACTIVE if loc is None else loc
            if graph.connected(n.find("LocationId")):
                out["row"] = True  # a row fixed at run time (the card's own, a chosen one)
        elif kind == "FilterCardTierNode":
            negate = (
                n.find("Negate") is not None and n.find("Negate").get("V") == "True"
            )
            mask = literal(graph, n, "Tier", 15)
            tiers &= (15 & ~mask) if negate else mask
        elif kind == "FilterCardTokensNode":
            if n.find("Negate") is not None and n.find("Negate").get("V") == "True":
                ignore |= literal(graph, n, "Tokens", 0)
        elif kind == "FilterCardPowerNode":
            value = graph.int_value(n.find("Power"))
            op = COMPARE.get(literal(graph, n, "Op", 0), "?")
            if n.find("Negate") is not None and n.find("Negate").get("V") == "True":
                op = NEGATED.get(op, op)
            reads = {d[1] for d in graph.data_deps(n) if d[0] == "type"}
            if value is None and op == "==" and "GetCardPowerNode" in reads:
                out["equal_power"] = True  # also every unit with the target's power
            else:
                out["power"] = f"{op}{value if value is not None else 'dynamic'}"
        elif kind == "FilterCardIdNode":
            negate = n.find("Negate") is not None and n.find("Negate").get("V") == "True"
            reads = {d[1] for d in graph.data_deps(n) if d[0] == "type"}
            if negate and "GetAbilityOwnerCardNode" in reads:
                out["excludes_self"] = True
        elif kind == "FilterCardPowerStateNode":
            negate = (
                n.find("Negate") is not None and n.find("Negate").get("V") == "True"
            )
            state = POWER_STATE.get(literal(graph, n, "PowerState", 0), "?")
            out["power_state"] = ("not_" if negate else "") + state
    out["tiers"] = bits(tiers, TIERS)
    if ignore:
        out["skips"] = bits(ignore, CARD_TOKENS)
    if types["DrawCardsNode"]:
        out["zone"] = "hand_or_deck"  # cards just drawn
    elif locations & GRAVEYARD and not locations & ACTIVE:
        out["zone"] = "graveyard"
    elif locations & (HAND | DECK) and not locations & ACTIVE:
        out["zone"] = "hand_or_deck"
    return out


def amount_source(graph, port):
    """A run-time amount, named by what it reads: "dynamic:<node type>"."""
    for node, _ in upstream_nodes(graph, port.get("Id")) if port is not None else ():
        if node.get("Type") not in ("GetVarNode", "SetVarNode", "CardListGetElementNode", "IntPlusNode", "IntMinusNode"):
            return "dynamic:" + node.get("Type")
    return "dynamic"


def loop_hits(graph, nid):
    """How many times a fixed loop (IntForeachNode) runs the effect, else None."""
    for loop in graph.of_type("IntForeachNode"):
        body = loop.find("FlowFor")
        starts = [graph.port[d][0] for d in graph.dst.get(body.get("Id"), ())] if body is not None else []
        if any(nid in graph.flow_reach(s) for s in starts):
            num = graph.int_value(loop.find("Num"))
            return num if num is not None else "dynamic"
    return None


def trigger_word(e, template):
    kinds = {t.kind for t in e["triggers"]}
    board = [
        t for t in e["triggers"] if not t.locs or any(loc & ACTIVE for loc in t.locs)
    ]
    if "PlayedTrigger" in kinds:
        return "deploy"
    if kinds & {"KilledTrigger", "BeforeDestroyedTrigger"}:
        return "deathwish"
    if any(t.kind == "TimerTriggered" and not t.repeats for t in board):
        return "timer"
    if any(t.repeats for t in board):
        return "engine"
    return "other" if kinds else "none"


def scope_word(sides):
    sides = set(sides) - {UNKNOWN}
    if {OWN, OPP} <= sides:
        return "board"
    if sides == {OPP}:
        return "opponent"
    return "own" if sides else "unknown"


def classify(graph, node, e, template):
    """One harmful effect as an answer kind with its parameters, or None."""
    kind = e["kind"]
    sides = e["sides"]
    if OPP not in sides:
        return None  # the same rule as the removal role
    port = (
        node.find("Targets") if node.find("Targets") is not None else node.find("Cards")
    )
    sel = selector(graph, port.get("Id")) if port is not None else {"select": "?"}
    if sel.get("zone") == "hand_or_deck":
        return None  # cards in a hand or deck, not on the board
    if sel.get("zone") == "graveyard" and kind != "banish":
        return None
    entry = {"scope": scope_word(sides), **sel, "trigger": trigger_word(e, template)}
    if "modal" in e["gates"]:
        entry["mode"] = "some"
    if kind == "destroy":
        threshold = row_threshold(graph) if sel.get("row") else None
        if sel["select"] == "highest" and threshold:
            entry["row_score"] = f"{threshold[0]}{threshold[1]}"
            return {"kind": "destroy_highest_row_threshold", **entry}
        if sel["select"] in ("highest", "lowest"):
            return {"kind": f"destroy_{sel['select']}", **entry}
        if sel.get("zone") == "graveyard":
            return None
        return {"kind": "destroy_target", **entry}
    if kind == "consume":
        return {"kind": "consume", **entry} if sel.get("zone") != "graveyard" else None
    if kind in ("damage", "set", "multiply"):
        amount = graph.int_value(node.find("Power"))
        entry["amount"] = amount if amount is not None else amount_source(graph, node.find("Power"))
        hits = loop_hits(graph, node.get("Id"))
        if hits is not None:
            entry["hits"] = hits
        power_type = POWER_TYPE.get(literal(graph, node, "TargetedPower", 0), "?")
        if (
            node.find("IgnoreArmor") is not None
            and node.find("IgnoreArmor").get("V") == "True"
        ):
            entry["ignores_armor"] = True
        if kind == "set":
            return {"kind": "set_power", **entry}
        if kind == "multiply":
            return {"kind": "multiply_power", **entry}  # amount in percent
        if power_type in ("base", "permanent"):
            return {"kind": "weaken", **entry}
        name = {
            "chosen": "damage_fixed",
            "event": "damage_fixed",
            "random": "damage_random",
            "row": "damage_row",
            "adjacent": "damage_row",
            "all": "damage_all",
            "highest": "damage_highest",
            "lowest": "damage_lowest",
        }.get(sel["select"], "damage_fixed")
        if name == "damage_all" and sel.get("row"):
            name = "damage_row"  # all units of one row
        return {"kind": name, **entry}
    if kind in ("reset", "transform"):
        if kind == "reset" and sel.get("power_state") == "wounded":
            return None  # resetting only damaged units heals them (CardPower.Reset)
        entry["reset"] = bits(literal(graph, node, "ResetMode", 0xFF), RESET_BITS)
        if kind == "transform":
            ref = graph.template_ref(node.find("Definition"), template.id)
            entry["into"] = sorted(ref) if isinstance(ref, set) else "dynamic"
        return {"kind": kind, **entry}
    if kind == "banish":
        return (
            {"kind": "banish", **entry}
            if sel.get("zone") != "graveyard"
            else {"kind": "banish_graveyard", **entry}
        )
    if kind == "lock":
        return {"kind": "lock", **entry}
    return None


def answer_kinds(template, graph, spawned_kinds=None):
    """The answers one card gives against opponent units: a list of entries
    {"kind": ..., parameters}. `spawned_kinds` {template id: [entries]} adds the
    answers of a small pool of cards it creates, tagged `via`."""
    out = []
    for e in facts_of(template, graph) if graph is not None else ():
        node = graph.nodes[e["node"]]
        if e["kind"] == "move":
            entry = _move_answer(graph, node, e, template)
        elif e["kind"] == "weather":
            entry = _weather_answer(graph, node, e, template)
        else:
            entry = classify(graph, node, e, template)
        if entry and entry not in out:
            out.append(entry)
    for tid, entries in sorted((spawned_kinds or {}).items()):
        for entry in entries:
            tagged = {**entry, "via": tid}
            if tagged not in out:
                out.append(tagged)
    return out


def _move_answer(graph, node, e, template):
    if OPP not in e["sides"]:
        return None
    port = node.find("Cards")
    sel = selector(graph, port.get("Id")) if port is not None else {"select": "?"}
    if sel.get("zone") in ("graveyard", "hand_or_deck"):
        return None
    entry = {
        "scope": scope_word(e["sides"]),
        **sel,
        "trigger": trigger_word(e, template),
    }
    if "modal" in e["gates"]:
        entry["mode"] = "some"
    to, to_side = e.get("to"), e.get("to_side", frozenset())
    if OWN in to_side and OPP not in to_side and (to or ACTIVE) & ACTIVE:
        return {"kind": "seize", **entry}
    if to is not None and not to & ACTIVE and to & (HAND | DECK):
        return {"kind": "return", **entry}
    if (to or ACTIVE) & ACTIVE:
        return {"kind": "move_row", **entry}  # moves enemy units between their rows
    return None


def _weather_answer(graph, node, e, template):
    if OPP not in e["sides"]:
        return None
    if literal(graph, node, "Operation", 0) not in (0, 2):
        return None  # removes row tokens: a clear, not an answer
    tokens = node.find("Tokens")
    mask = graph.int_value(tokens) if tokens is not None else None
    entry = {
        "kind": "weather",
        "scope": scope_word(e["sides"]),
        "trigger": trigger_word(e, template),
    }
    entry["tokens"] = bits(mask, LOCATION_TOKENS) if mask else "random"
    if "modal" in e["gates"]:
        entry["mode"] = "some"
    return entry


# ---------------------------------------------------------------- weather tokens


def weather_tokens(token_graphs):
    """What each row token does to the units on its row, per its own ability:
    {name: [{"op": damage|boost, "select", "ties", "amount", "when"}]}."""
    out = {}
    for g in token_graphs:
        mask = int(g.attrib.get("LocationToken", 0))
        name = "/".join(bits(mask, LOCATION_TOKENS)) or str(mask)
        effects = []
        for node in g.of_type("ChangePowerNode"):
            op = POWER_OP.get(literal(g, node, "Operation", 1), "?")
            sel = selector(g, node.find("Targets").get("Id"))
            when = sorted(
                {
                    t.get("Type")
                    for t in g.nodes.values()
                    if t.get("Type", "").endswith("Trigger")
                }
            )
            amount = g.int_value(node.find("Power"))
            entry = {
                "op": "damage" if op == "remove" else "boost" if op == "add" else op,
                "select": sel["select"],
                "amount": amount if amount is not None else "dynamic",
                "when": when,
            }
            for key in ("ties", "pick", "count", "skips"):
                if key in sel:
                    entry[key] = sel[key]
            if entry not in effects:
                effects.append(entry)
        out[name] = effects
    return dict(sorted(out.items()))


# ---------------------------------------------------------------- exposure


def exposure(template, graph, card_roles, card_flags):
    """What a card on its owner's board is exposed to, from its own graph:
    a list of tags with parameters."""
    tags = []

    def add(tag):
        if tag not in tags:
            tags.append(tag)

    if template.tokens & 1:
        add({"tag": "value_to_come", "via": "resilience", "lost_to": ["lock"]})
    if graph is None:
        return tags
    for e in facts_of(template, graph):
        node = graph.nodes[e["node"]]
        trig = trigger_word(e, template)
        modal = "modal" in e["gates"]
        if (
            e["kind"] in ("boost", "set", "multiply")
            and e["sides"] <= {SELF}
            and e["sides"]
        ):
            power_type = POWER_TYPE.get(literal(graph, node, "TargetedPower", 0), "?")
            amount = graph.int_value(node.find("Power"))
            amount = amount if amount is not None else "dynamic"
            if e["kind"] == "boost" and power_type == "current":
                add(
                    {
                        "tag": "boosts_self",
                        "amount": amount,
                        "trigger": trig,
                        "lost_to": ["reset", "transform"],
                        **({"mode": "some"} if modal else {}),
                    }
                )
            elif e["kind"] == "boost":
                add(
                    {
                        "tag": "strengthens_self",
                        "amount": amount,
                        "trigger": trig,
                        "lost_to": ["reset:template_power", "transform"],
                        **({"mode": "some"} if modal else {}),
                    }
                )
            else:
                add(
                    {
                        "tag": f"{e['kind']}_self",
                        "amount": amount,
                        "trigger": trig,
                        **({"mode": "some"} if modal else {}),
                    }
                )
            if modal:
                add({"tag": "modal_tall", "trigger": trig})
        if (
            modal
            and e["kind"] in ("damage", "destroy", "lock", "reset")
            and OPP in e["sides"]
        ):
            add({"tag": "modal_spread", "effect": e["kind"]})
        if (
            e["kind"] in ("damage", "destroy")
            and OWN in e["sides"]
            and OPP not in e["sides"]
        ):
            amount = (
                graph.int_value(node.find("Power")) if e["kind"] == "damage" else None
            )
            add(
                {
                    "tag": "self_damage",
                    "effect": e["kind"],
                    "amount": amount if amount is not None else "dynamic",
                    "trigger": trig,
                    "use": "breaks a tie for a destroy-highest answer",
                }
            )
        if e["kind"] == "armor" and e["sides"] & {SELF, OWN}:
            amount = graph.int_value(node.find("Armor"))
            add(
                {
                    "tag": "armor",
                    "amount": amount if amount is not None else "dynamic",
                    "absorbs": "damage",
                }
            )
        if trig in ("engine", "timer", "deathwish") and e["kind"] not in (
            "timer",
            "tokens",
        ):
            if e["kind"] in HARM and OPP not in e["sides"]:
                continue  # harm to its own side (an enabler's cost) is not value to come
            amount = (
                graph.int_value(node.find("Power"))
                if node.find("Power") is not None
                else None
            )
            add(
                {
                    "tag": "value_to_come",
                    "via": trig,
                    "effect": e["kind"],
                    "amount": amount if amount is not None else "dynamic",
                    **({"timer": template.timer} if trig == "timer" else {}),
                    "lost_to": ["lock", "banish", "transform"]
                    if trig != "deathwish"
                    else ["lock", "banish"],
                }
            )
    if "comes_back" in card_flags:
        add({"tag": "recoverable", "via": "comes_back", "lost_to": ["lock", "banish"]})
    if "deathwish" in card_roles:
        add({"tag": "recoverable", "via": "deathwish", "lost_to": ["lock", "banish"]})
    return tags


# ---------------------------------------------------------------- revive reach


def revive_reach(template, graph, pool):
    """What a card can bring back from a graveyard: a list of
    {"from": own|opp|both, "types", "tiers", "to": play|hand|deck, "targets"}.
    `targets` is the explicit template list when the read is narrowed by a
    category or id (else null: types and tiers say it)."""
    out = []
    if graph is None:
        return out
    for e in facts_of(template, graph):
        if e["kind"] not in ("play", "summon", "move"):
            continue
        for origin in e["origins"]:
            if origin["kind"] != "GetCardsNode" or not (origin["loc"] or 0) & GRAVEYARD:
                continue
            side = set(origin["side"]) - {UNKNOWN}
            side_word = (
                "both" if {OWN, OPP} <= side else "opp" if side == {OPP} else "own"
            )
            to = (
                "play"
                if e["kind"] in ("play", "summon")
                else (
                    "hand"
                    if (e.get("to") or 0) & HAND
                    else "deck"
                    if (e.get("to") or 0) & DECK
                    else "play"
                    if (e.get("to") or ACTIVE) & ACTIVE
                    else "other"
                )
            )
            if to == "other":
                continue
            preds, narrow = [], False
            for slot_id, keep in origin["filters"]:
                if slot_id is None:
                    continue
                pred, notes = graph.filter_predicate(slot_id, template.id)
                narrow |= keep and "narrow" in notes
                if pred is not None:
                    preds.append(pred if keep else (lambda t, p=pred: not p(t)))
            types, tiers = origin["types"] or UNIT_TYPE, origin["tiers"] or 15
            targets = None
            if narrow:
                own_faction = side_word == "own" and template.faction != NEUTRAL
                targets = sorted(
                    t.id
                    for t in pool.values()
                    if t.type & types
                    and t.tier & tiers
                    and all(p(t) for p in preds)
                    and (not own_faction or t.faction in (template.faction, NEUTRAL))
                )
            if to == "deck" and owner_position(graph, graph.nodes[e["node"]]):
                to = "owner_deck"  # back into the deck of the card's owner
            entry = {
                "from": side_word,
                "types": bits(types, TYPES),
                "tiers": bits(tiers, TIERS),
                "to": to,
                "targets": targets,
                "trigger": trigger_word(e, template),
            }
            if entry not in out:
                out.append(entry)
    return out


def owner_position(graph, node):
    """True when a move's destination player is the moved card's own owner."""
    position = node.find("Position")
    if position is None:
        return False
    return any(n.get("Type") == "GetCardPlayerIdNode" for n, _ in upstream_nodes(graph, position.get("Id")))


def reaches(reach, template, reviver_faction):
    """True when one revive_reach entry can bring back `template` (a Template)."""
    if reach["targets"] is not None:
        return template.id in reach["targets"]
    if not any(template.type & bit for bit, name in TYPES if name in reach["types"]):
        return False
    if not any(template.tier & bit for bit, name in TIERS if name in reach["tiers"]):
        return False
    if reach["from"] == "own" and reviver_faction != NEUTRAL:
        return template.faction in (reviver_faction, NEUTRAL)
    return True


def chooses(graph, flags):
    """True when the card gets one of its spawn options (a choose-one or a
    random pick), not all of them."""
    if "modal" in flags:
        return True
    kinds = {n.get("Type") for n in graph.nodes.values()} if graph is not None else set()
    return bool(kinds & {"RequestTemplateChoiceNode", "CardDefinitionListGetRandomElementNode", "CardDefinitionListShuffleNode"})


def annotate(data, client, cards):
    """Add answer kinds, exposure tags, revive reach and replayable_by to
    roles.json content `data` (from `card_roles.build`); `cards` are the
    derived Cards (for the templates each one spawns)."""
    templates, graphs, token_graphs = client
    collectible = {i: t for i, t in templates.items() if t.availability == clientdata.BASE_SET}
    memo = {}

    def own(tid):
        if tid not in memo:
            memo[tid] = answer_kinds(templates[tid], graphs.get(tid))
        return memo[tid]

    reach = {}
    for tid, card in cards.items():
        entry = data["cards"][str(tid)]
        options = sorted(i for i in card.spawned if i in templates)
        spawned = {}
        if 0 < len(options) <= SPAWN_POOL:
            for i in options:
                kinds = own(i)
                if len(options) > 1 and chooses(graphs.get(tid), entry["flags"]):
                    kinds = [{**k, "mode": "some"} for k in kinds]
                spawned[i] = kinds
        entry["answer_kinds"] = answer_kinds(templates[tid], graphs.get(tid), spawned)
        tags = exposure(templates[tid], graphs.get(tid), entry["roles"], entry["flags"])
        for i in options if len(options) <= SPAWN_POOL else ():
            spawned_card = data["cards"].get(str(i), {"roles": [], "flags": []})
            for tag in exposure(templates[i], graphs.get(i), spawned_card["roles"], spawned_card["flags"]):
                tagged = {**tag, "via": i} if "via" not in tag else {**tag, "spawned": i}
                if tagged not in tags:
                    tags.append(tagged)
        entry["exposure"] = tags
        entry["revive_reach"] = revive_reach(templates[tid], graphs.get(tid), collectible)
        if entry["revive_reach"]:
            reach[tid] = entry["revive_reach"]
    for tid in cards:
        entry = data["cards"][str(tid)]
        if not entry["answer_kinds"]:
            continue
        target = templates[tid]
        entry["replayable_by"] = sorted(
            {
                (reviver, r["from"], r["to"])
                for reviver, entries in reach.items()
                for r in entries
                if reaches(r, target, templates[reviver].faction)
            }
        )
        entry["replayable_by"] = [{"id": i, "from": side, "to": to} for i, side, to in entry["replayable_by"]]
    data["weather_tokens"] = weather_tokens(token_graphs)
    data["schema"]["version"] = 4
    data["schema"]["card"].update(SCHEMA_FIELDS)
    data["schema"]["weather_tokens"] = "per row token: what its own ability does to units on its row each turn (op, select, ties, pick, skips, when)"
    data["schema"]["(numbers)"] = (
        "numeric ability parameters (amounts, levels, counts, hits, row-score and power thresholds, "
        "timers) are client data: scripts/answer_kinds.py writes them from the user's client to "
        ".local/cards/answer-params/<roles sha>.json and readers join them (answer_kinds.load_params, CardRoles)"
    )
    return data


# ---------------------------------------------------------------- numbers stay local
#
# Card stats and numeric ability parameters are CDPR data and stay on the
# user's machine. The roles.json the build writes keeps the structure; the
# numbers go to a file in .local, index aligned with roles.json's lists, and
# are joined at run or build time.

PARAMS_DIR = clientdata.ROOT / ".local" / "cards" / "answer-params"  # one file per roles.json version
ROLES = clientdata.ROOT / "data" / "cards" / "roles.json"
NUMERIC = {
    "answer_kinds": ("amount", "levels", "count", "hits", "row_score", "power"),
    "exposure": ("amount", "timer"),
    "weather_tokens": ("amount", "count"),
}


def _cut(entries, keys):
    """(entries without `keys`, [the cut values per entry]); a power filter
    keeps its compare as `power_op`."""
    kept, cut = [], []
    for e in entries:
        numbers = {k: e[k] for k in keys if k in e}
        rest = {k: v for k, v in e.items() if k not in keys}
        if "power" in numbers:
            rest["power_op"] = "".join(ch for ch in str(numbers["power"]) if ch in "<>=!")
        kept.append(rest)
        cut.append(numbers)
    return kept, cut


def split(data):
    """(roles.json content without numbers, the numbers for PARAMS)."""
    params = {"source": "scripts/answer_kinds.py from the user's client: numeric parameters of roles.json's answer_kinds, exposure and weather_tokens (index aligned), prerequisite thresholds ({tag: value}) and choose-one option counts; client data, stays in .local", "cards": {}, "weather_tokens": {}}
    for tid, card in data["cards"].items():
        numbers = {}
        if card.get("prerequisite_numbers"):
            numbers["prerequisites"] = card.pop("prerequisite_numbers")
        else:
            card.pop("prerequisite_numbers", None)
        if card.get("modes", {}).get("options") is not None:
            numbers["mode_options"] = card["modes"].pop("options")
        for field in ("answer_kinds", "exposure"):
            if card.get(field):
                card[field], cut = _cut(card[field], NUMERIC[field])
                if any(cut):
                    numbers[field] = cut
        if numbers:
            params["cards"][tid] = numbers
    for name, effects in data.get("weather_tokens", {}).items():
        data["weather_tokens"][name], cut = _cut(effects, NUMERIC["weather_tokens"])
        params["weather_tokens"][name] = cut
    return data, params


def join(data, params):
    """roles.json content with the local numbers merged back in (in place)."""
    for tid, numbers in params.get("cards", {}).items():
        card = data["cards"].get(tid)
        for field, cut in numbers.items() if card else ():
            if field == "prerequisites":
                card["prerequisite_numbers"] = dict(cut)
            elif field == "mode_options":
                card.setdefault("modes", {})["options"] = cut
            else:
                for entry, values in zip(card.get(field, []), cut):
                    entry.update(values)
    for name, cut in params.get("weather_tokens", {}).items():
        for entry, values in zip(data.get("weather_tokens", {}).get(name, []), cut):
            entry.update(values)
    return data


def roles_text(data):
    """roles.json's text exactly as written (the params file is tied to it)."""
    return json.dumps(data, indent=1, ensure_ascii=False) + "\n"


def text_sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def params_path(sha, directory=PARAMS_DIR):
    """The local numbers for the roles.json with this SHA-256. `.local` is
    shared by every worktree, so each roles.json version has its own file and
    branches never overwrite each other's numbers."""
    return Path(directory) / f"{sha[:16]}.json"


def load_params(roles=ROLES, directory=PARAMS_DIR):
    """The local numbers for this checkout's roles.json, or empty when they
    were never derived for it (no client, or run scripts/answer_kinds.py)."""
    sha = text_sha(Path(roles).read_text(encoding="utf-8"))
    path = params_path(sha, directory)
    if not path.is_file():
        return {}
    params = json.loads(path.read_text(encoding="utf-8"))
    if params.get("roles_sha256") != sha:
        raise SystemExit(f"{path} does not match its roles.json; run scripts/answer_kinds.py")
    return params


def write_params(params, data, directory=PARAMS_DIR):
    """Write the numbers, tied to the structural roles.json content `data`;
    returns the file."""
    sha = text_sha(roles_text(data))
    path = params_path(sha, directory)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({**params, "roles_sha256": sha}, indent=1) + "\n")
    return path


def main():
    """Write only the local numbers (setup and build time; needs the client)."""
    import card_roles

    data, params = split(card_roles.build(card_roles.load_client()))
    path = write_params(params, data)
    print(f"{len(params['cards'])} cards -> {path}")


SCHEMA_FIELDS = {
    "answer_kinds": "answers it gives against opponent units: [{kind, scope, select, ties, row, power_op, equal_power, excludes_self, pick, power_state, tiers, skips, reset, into, tokens, trigger, mode, via}]; the numbers (amount, levels, count, hits, row_score, power) are local (.local/cards/answer-params/<roles sha>.json)",
    "exposure": "what the card on its own board is exposed to or protected by: [{tag, ...}] (boosts_self, strengthens_self, value_to_come, recoverable, modal_tall, modal_spread, self_damage, armor); amounts and timers are local",
    "revive_reach": "what it can bring back from a graveyard: [{from own|opp|both, types, tiers, to play|hand|deck|owner_deck, targets (narrow reads) or null, trigger}]",
    "replayable_by": "answer cards only: revive cards whose reach covers this card [{id, from, to}] (to owner_deck: back into its owner's deck); leaders included",
}


if __name__ == "__main__":
    main()
