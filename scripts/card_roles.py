#!/usr/bin/env python3
"""Derive card roles from the structure of the client's ability graphs.

    scripts/card_roles.py                 # every collectible card -> data/cards/roles.json
    scripts/card_roles.py --roster        # only the cards of the roster decks
    scripts/card_roles.py --review SHEET.md          # also refresh a label sheet
    scripts/card_roles.py --import-review SHEET.md   # the sheet's labels -> roles-human.json
    scripts/card_roles.py --check SAMPLE.json        # score against a labelled sample

The script reads `Templates.xml` and `Abilities.xml` from the user's own
client (`data_definitions`). It reads template
fields and the ability graphs: node types, enum parameters, template ids and
the data-flow connections between node ports. It never reads card text. The
output holds role labels, template ids, project card names (the names the
roster deck files use, else the client's English name). It stores no card
stats: tier, power and faction come from the client at run time.

The derivation:

- Side trace. For an effect node (damage, destroy, lock, ...) the script
  walks the graph backwards from the effect's target port to the nodes that
  fix whose cards they are: the owner's or the opponent's player id, the
  ability's own card, a "both players" literal. Variables (SetVar/GetVar and
  list appends) are followed. A spy's graph is written from the side it lands
  on, so a spy's sides are swapped back to the side of the player who plays it.
- Tutor resolution. For a play, summon or move node the script walks back to
  the `GetCardsNode`s that read a deck and collects the filters on the way
  (category masks, template ids, tier, type, faction, base power). It
  evaluates them against every collectible template of the card's faction and
  Neutral, so category tutors resolve to concrete templates. A copy tutor
  (a copy of an ally chosen in play) keeps the filters that chose the ally.
- Triggers. The flow from each trigger says where the card must be, whether
  the trigger fires again and again (not a once-only latch: a condition that
  reads state the same flow writes afterwards; not an ambush flip; a timer
  only when it re-arms itself), and which conditions gate a deploy effect
  (game state, a choose-one, or a local check).

Optional local label overrides in `data/cards/roles-human.json` are merged last
("by" names who set the label):
    {"<template id>": {"name": "...", "by": "...",
                       "roles": [...]  (replaces the derived roles)
                       or "add": [...], "remove": [...],
                       "flags": {"comes_back": true}, "note": "..."}}
`--import-review` writes these entries from the label sheet's label column.
"""

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass, field
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

import clientdata

OUT = clientdata.ROOT / "data" / "cards" / "roles.json"
HUMAN = clientdata.ROOT / "data" / "cards" / "roles-human.json"
DECK_CONTEXT = clientdata.ROOT / "data" / "cards" / "deck-context.json"
ROSTER = clientdata.ROOT / "data" / "decks" / "roster"
ROSTER_DECKS = len(list(ROSTER.glob("*.json")))

ROLES = {
    "spy": "placed on the opponent's side",
    "tutor": "plays or summons other cards from its owner's deck",
    "thin_copy": "summons copies of its own template from the deck",
    "draw": "moves cards from a deck to a hand",
    "target": "a template a tutor names by id: weak to draw while the tutor is in the deck",
    "engine": "an effect that fires again and again while the card is on the board (turn, re-armed timer or event trigger; not once-only latches, not self-harm)",
    "timer": "an effect that fires once when the card's timer on the board runs out (answer it before then)",
    "enabler": "damages, destroys or consumes its owner's other units, or fires an ally's ability again (feeds engines)",
    "discard": "moves its owner's deck or hand cards to the graveyard (feeds comes_back and revive cards)",
    "removal": "damages, destroys, banishes, resets, transforms, locks, seizes or weakens opponent cards",
    "weather": "adds or clears row hazards, or a weather ability names it",
    "carryover": "keeps value into the next round (Resilience, round triggers on the board, resurrects at a round start)",
    "revive": "plays, summons or moves cards out of a graveyard",
    "create": "spawns templates other than its own (gains the removal, weather, tutor, draw and revive roles of what it spawns)",
    "deathwish": "acts when it is destroyed",
    "points": "boosts, strengthens, heals or armors its own side, or sets its own power, when deployed",
    "handbuff": "boosts cards in its owner's hand or deck",
    "reveal": "reveals cards in a hand",
    "finisher": "heuristic: no other role and high base power (always low confidence)",
    "body": "no ability",
}
FLAGS = {
    "comes_back": "a trigger in the graveyard plays or moves the card itself back",
    "ambush": "enters face down (Ambush token) and scores nothing until it flips",
    "self_summon": "a trigger in the deck plays or summons the card itself",
    "conditional": "a deploy effect depends on the game state (deck shape, score, passing, how it was played, row tokens)",
    "modal": "a deploy effect is one option of a choose-one",
}
REVIEW_LABELS = sorted(ROLES) + sorted(FLAGS) + ["ok", "none"]

TIER = {1: "leader", 2: "bronze", 4: "silver", 8: "gold"}
FACTION = {1: "Neutral", **clientdata.FACTIONS}
DECK, GRAVEYARD, HAND, ACTIVE = 0x10, 0x20, 0x8, 0x7
UNIT_TYPE = 4
RESILIENCE, LOCK, AMBUSH, REVEALED = 1, 4, 8, 0x40
NEUTRAL = 1
OWN, OPP, SELF, UNKNOWN = "own", "opp", "self", "?"
SELF_NODE = ET.Element("e", Type="GetAbilityOwnerCardNode", Id="self")  # "the card itself" as an origin
COPY = "copy"  # a template reference to a card chosen at run time
SIDE_TYPES = {
    "PlayerIdVar",
    "CardVar",
    "CardListVar",
    "CardPositionVar",
    "LocationVar",
    "LocationListVar",
}
CARD_TYPES = {"CardVar", "CardListVar"}
OUTPUT_TAGS = {"Out", "DV"}
# Inputs that name who acts, not whose cards are acted on.
ACTOR_PORTS = {"Attacker", "Spawner", "Requester", "Summoner"}
# Trigger outputs that are the ability's own card.
SELF_OUTPUTS = {
    ("PlayedTrigger", "CardPlayed"),
    ("KilledTrigger", "CardKilled"),
    ("TimerTriggered", "Card"),
    ("TimerChanged", "Card"),
}
TRIGGER_PLAYER_PORTS = {
    "FromPlayer",
    "ToPlayer",
    "PlayerMask",
    "PlayerId",
    "PlayerIdMask",
}
LIST_WRITERS = {
    "CardListAppendNode": "ListToAppend",
    "CardListAddNode": "Element",
    "LocationListAddNode": "Element",
}
# Triggers that fire once (deploy, death, round edges, mulligan): their
# effects are not an engine.
ONE_SHOT = {
    "PlayedTrigger",
    "KilledTrigger",
    "BeforeRoundTrigger",
    "AfterRoundTrigger",
    "AfterMulliganTrigger",
}
TIMER_TRIGGERS = {"TimerTriggered", "TimerChanged"}
ROUND_TRIGGERS = {"BeforeRoundTrigger", "AfterRoundTrigger"}
TURN_TRIGGERS = {"BeforeTurnTrigger", "AfterTurnTrigger", "BeforeTurnEndTrigger"}
ENGINE_EFFECTS = {"boost", "damage", "spawn", "summon", "play", "destroy", "armor", "set", "multiply"}
GATED_EFFECTS = {"boost", "set", "multiply", "spawn", "summon", "play", "damage", "destroy"}
DEFINITION_PASSTHROUGH = {
    "CardDefinitionListGetElementNode",
    "CardDefinitionListGetRandomElementNode",
    "CardDefinitionListShuffleNode",
    "CardDefinitionListGetRangeNode",
    "CardDefinitionListElementToListNode",
    "CardDefinitionListRemoveNode",
    "RequestTemplateChoiceNode",
}
CONDITIONS = {
    "LocationListBranchNode",
    "BranchNode",
    "IntBranchNode",
    "HasNCopiesOfDefinitions",
    "BranchGameStateNode",
    "CardDefinitionListBranchNode",
}
# Condition inputs that make a deploy effect depend on the game state.
STATE_SOURCES = {
    "PlayerGetHasPassedNode",
    "PlayerGetScoreNode",
    "GetLocationScore",
    "GetInitialDeckDefinitions",
    "HasNCopiesOfDefinitions",
    "GetMatchingPlayedCardsNode",
    "BranchGameStateNode",
    "GetLocationTokenOfType",
    "GetLocationToken",
    "GetLocationsByTokenTypeNode",
    "GetCardsPlayedInCurrentTurnNode",
    "CardsOffBoard",
    "RowTokenQuery",
}
PROPAGATED = {"removal", "weather", "tutor", "draw", "revive"}
SPAWN_POOL = 6
HARMFUL = {
    "damage",
    "destroy",
    "banish",
    "reset",
    "transform",
    "lock",
    "set",
    "multiply",
}


def _children(node, name):
    el = node.find(name)
    return list(el) if el is not None else []


def is_trigger(kind):
    return kind.endswith("Trigger") or kind in ("TimerTriggered", "TimerChanged")


# ---------------------------------------------------------------- loading


@dataclass
class Template:
    id: int
    name: str
    availability: int
    faction: int
    tier: int
    type: int
    tokens: int
    power: int
    timer: int
    own_side: int
    opp_side: int
    categories: tuple
    deathwish: bool = False  # its graph has a KilledTrigger
    deploy: bool = False  # its graph has a PlayedTrigger

    @property
    def spy(self):
        """A unit that lands on the opponent's side. Specials aimed at the
        opponent's rows (weather, Muzzle) share the placement but not the role."""
        return self.type == UNIT_TYPE and self.own_side == 0 and self.opp_side != 0


def parse_templates(text, names=None):
    names = names or {}
    templates = {}
    for el in ET.fromstring(text).iter("Template"):
        if el.get("Type") != "CardTemplate":
            continue
        place = el.find("Placement")
        number = lambda tag: int(el.findtext(tag))
        tid = int(el.get("Id"))
        templates[tid] = Template(
            id=tid,
            name=names.get(tid, el.get("DebugName")),
            availability=int(el.get("Availability")),
            faction=number("FactionId"),
            tier=number("Tier"),
            type=number("Type"),
            tokens=number("Tokens"),
            power=number("Power"),
            timer=number("InitialTimer"),
            own_side=int(place.get("PlayerSide")),
            opp_side=int(place.get("OpponentSide")),
            categories=tuple(int(e.get("V")) for e in el.find("Categories")),
        )
    return templates


def load_client(definitions=clientdata.DEFINITIONS):
    """Templates, card ability graphs and weather-token abilities of the client."""
    with zipfile.ZipFile(definitions) as archive:
        names = {
            int(k): v
            for k, v in re.findall(
                r'^"cards";"(\d+)_name";"(.*)"\r?$',
                archive.read("Localization/en_us.csv").decode("utf-8-sig"),
                re.M,
            )
        }
        templates = parse_templates(
            archive.read("Templates.xml").decode("utf-8-sig"), names
        )
        root = ET.fromstring(archive.read("Abilities.xml").decode("utf-8-sig"))
    graphs, token_graphs = {}, []
    for ability in root.iter("Ability"):
        if ability.get("Type") == "CardAbility":
            graphs[int(ability.get("Template"))] = Graph(ability)
        elif ability.get("Type") == "LocationTokenAbility":
            token_graphs.append(Graph(ability))
    for tid, graph in graphs.items():
        if tid in templates:
            templates[tid].deathwish = bool(graph.of_type("KilledTrigger"))
            templates[tid].deploy = bool(graph.of_type("PlayedTrigger"))
    return templates, graphs, token_graphs


# ---------------------------------------------------------------- graph


class Graph:
    """One ability: nodes, ports and the data/flow connections between ports."""

    def __init__(self, ability):
        self.attrib = dict(ability.attrib)  # e.g. a row token ability's LocationToken
        self.nodes = {}
        self.port = {}  # port id -> (node element, port element)
        self.src = defaultdict(list)  # destination port id -> source port ids
        self.dst = defaultdict(list)  # source port id -> destination port ids
        self.var_decl = {}
        for scope in ("TemporaryVariables", "PersistentVariables"):
            for var in ability.find(scope) if ability.find(scope) is not None else ():
                self.var_decl[var.get("Id")] = var
        nodes = ability.find("Nodes")
        for node in nodes if nodes is not None else ():
            self.nodes[node.get("Id")] = node
            for child in node:
                if child.get("Id"):
                    self.port[child.get("Id")] = (node, child)
                for grandchild in child:
                    if grandchild.get("Id"):
                        self.port[grandchild.get("Id")] = (node, grandchild)
        connections = ability.find("Connections")
        for c in connections if connections is not None else ():
            self.src[c.get("DstId")].append(c.get("SrcId"))
            self.dst[c.get("SrcId")].append(c.get("DstId"))
        self.var_writers = self._var_writers()

    def of_type(self, *kinds):
        return [n for n in self.nodes.values() if n.get("Type") in kinds]

    def child(self, node, name):
        return node.find(name)

    def literal(self, node, name, default=None):
        el = node.find(name)
        return (
            int(el.get("V"))
            if el is not None and el.get("V", "").lstrip("-").isdigit()
            else default
        )

    def connected(self, port_el):
        return bool(port_el is not None and self.src.get(port_el.get("Id")))

    def _var_of(self, port_id):
        """The variable a list port reads, when it reads one directly."""
        for s in self.src.get(port_id, ()):
            node, _ = self.port[s]
            if node.get("Type") in ("GetVarNode", "SetVarNode"):
                return node.get("VarId")
        return None

    def _var_writers(self):
        writers = defaultdict(list)
        for node in self.nodes.values():
            kind = node.get("Type")
            if kind == "SetVarNode" and node.find("In") is not None:
                writers[node.get("VarId")].append(node.find("In").get("Id"))
            elif kind in LIST_WRITERS:
                target, element = node.find("List"), node.find(LIST_WRITERS[kind])
                if target is not None and element is not None:
                    var = self._var_of(target.get("Id"))
                    if var:
                        writers[var].append(element.get("Id"))
        return writers

    def inputs(self, node, types):
        """Input ports of a node carrying the given var types: connected ones,
        plus unconnected player-id literals."""
        for child in node:
            for port in [child, *child]:
                pid = port.get("Id")
                if not pid or port.get("Type") not in types:
                    continue
                if self.src.get(pid):
                    yield port
                elif port.get("Type") == "PlayerIdVar" and port.tag not in OUTPUT_TAGS:
                    yield port

    def upstream(self, port_id):
        """Source ports feeding a port, with variables resolved."""
        sources = list(self.src.get(port_id, ()))
        node, port = self.port[port_id]
        if not sources and node.get("Type") == "GetVarNode" and port.tag == "Out":
            sources = list(self.var_writers.get(node.get("VarId"), ()))
        return sources

    # ------------------------------------------------------------ sides

    def side(self, port_id, _seen=None):
        """Whose cards a port carries: a subset of {own, opp, self, ?}.

        `self` is the ability's own card; `?` is a source the static walk
        cannot place (the current turn's player, an attack's attacker)."""
        seen = _seen if _seen is not None else set()
        if port_id in seen:
            return frozenset()
        seen = seen | {port_id}
        node, port = self.port[port_id]
        kind = node.get("Type")
        sources = self.src.get(port_id)
        if sources:
            return frozenset().union(*(self.side(s, seen) for s in sources))
        if kind == "GetVarNode":
            writers = self.var_writers.get(node.get("VarId"), ())
            if not writers:
                return frozenset({UNKNOWN})
            return frozenset().union(*(self.side(s, seen) for s in writers))
        if port.get("Type") == "PlayerIdVar" and port.tag not in OUTPUT_TAGS:
            # An unconnected input: a literal player mask (3 = both players).
            value = int(port.get("V") or 0) & 3
            return frozenset({OWN, OPP} if value == 3 else {UNKNOWN} if value else ())
        # `port_id` is an output of `node`: compute it from the node.
        if kind in ("GetAbilityOwnerCardNode", "GetAbilityOwnerCardListNode"):
            return frozenset({SELF})
        if kind in ("GetOwnerPlayerIdNode", "GetOwnerLocationNode"):
            return frozenset({OWN})
        if kind in ("GetOwnerOpponentPlayerIdNode", "GetOpponentPlayerId"):
            return frozenset({OPP})
        if kind in ("GetCurrentPlayerIdNode", "GetAttackerNode", "GetTargetsNode"):
            return frozenset({UNKNOWN})
        if (kind, port.tag) in SELF_OUTPUTS:
            return frozenset({SELF})
        if is_trigger(kind):
            players = [
                p
                for p in self.inputs(node, {"PlayerIdVar"})
                if p.tag in TRIGGER_PLAYER_PORTS
            ]
            sides = (
                frozenset().union(*(self.side(p.get("Id"), seen) for p in players))
                if players
                else frozenset()
            )
            return sides or frozenset({UNKNOWN})
        if kind == "GetCardsInShapeNode":
            shape = node.find("Shape/V")
            both = shape is not None and shape.get("ApplyToBothSides") == "True"
            # The units in a shape around the card itself are its neighbours.
            base = frozenset(OWN if s == SELF else s for s in self._union_inputs(node, seen))
            return base | ({OWN, OPP} if both else set()) if base else frozenset({UNKNOWN})
        if kind == "FilterCardListNode":
            base = (
                self.side(node.find("Cards").get("Id"), seen)
                if node.find("Cards") is not None
                else frozenset()
            )
            narrow = self._player_filter(node, seen)
            if narrow is None:
                return base
            if port.tag == "NotFilteredCards":
                narrow = {OWN, OPP} - narrow
            return frozenset(
                s
                for s in base
                if s in narrow or (s == SELF and OWN in narrow) or s == UNKNOWN
            )
        sides = self._union_inputs(node, seen)
        return sides if sides else frozenset({UNKNOWN})

    def _union_inputs(self, node, seen):
        sides = frozenset()
        for port in self.inputs(node, SIDE_TYPES):
            if port.tag in ACTOR_PORTS or port.get("Id") in seen:
                continue
            sides |= self.side(port.get("Id"), seen)
        return sides

    def _player_filter(self, node, seen):
        """The sides a FilterCardListNode keeps by player-id filters, or None."""
        keep = None
        filters = node.find("Filters")
        for slot in filters if filters is not None else ():
            for s in self.src.get(slot.get("Id"), ()):
                fnode, _ = self.port[s]
                if fnode.get("Type") != "FilterCardPlayerIdNode":
                    continue
                sides = set(self.side(fnode.find("PlayerId").get("Id"), seen)) & {
                    OWN,
                    OPP,
                }
                if not sides:
                    continue
                if (
                    fnode.find("Negate") is not None
                    and fnode.find("Negate").get("V") == "True"
                ):
                    sides = {OWN, OPP} - sides
                keep = sides if keep is None else keep & sides
        return keep

    # ------------------------------------------------------------ provenance

    def origins(self, port_id, filters=(), _seen=None):
        """Where the cards on a port come from: [(source node, filters)], where
        filters are (filter port id, keep) pairs met on the way back."""
        seen = _seen if _seen is not None else set()
        if port_id in seen:
            return []
        seen = seen | {port_id}
        node, port = self.port[port_id]
        kind = node.get("Type")
        sources = (
            self.upstream(port_id)
            if (self.src.get(port_id) or kind == "GetVarNode")
            else None
        )
        if sources is not None and (self.src.get(port_id) or port.tag == "Out"):
            if not sources:
                return [(node, list(filters))]
            out = []
            for s in sources:
                out += self.origins(s, filters, seen)
            return out
        if kind in (
            "GetCardsNode",
            "GetAbilityOwnerCardNode",
            "GetAbilityOwnerCardListNode",
        ) or is_trigger(kind):
            return [(node, list(filters))]
        if kind == "FilterCardListNode" and port.tag != "NotFilteredCards" and self._keeps_only_self(node):
            return [(SELF_NODE, list(filters))]
        if kind == "FilterCardListNode":
            keep = port.tag != "NotFilteredCards"
            slots = node.find("Filters")
            added = [
                (slot.get("Id"), keep) for slot in (slots if slots is not None else ())
            ]
            cards = node.find("Cards")
            return (
                self.origins(cards.get("Id"), [*filters, *added], seen)
                if cards is not None
                else []
            )
        if kind == "FilterPowerLevelNode":  # highest or lowest: fixed by the deck
            filters = [*filters, (None, "level")]
        out = []
        for p in self.inputs(node, CARD_TYPES):
            if p.tag not in ACTOR_PORTS:
                out += self.origins(p.get("Id"), filters, seen)
        return out or [(node, list(filters))]

    def _keeps_only_self(self, node):
        """A FilterCardListNode with a card-id filter on the ability's own card."""
        for slot in _children(node, "Filters"):
            for s in self.src.get(slot.get("Id"), ()):
                fnode, _ = self.port[s]
                negate = fnode.find("Negate") is not None and fnode.find("Negate").get("V") == "True"
                if fnode.get("Type") != "FilterCardIdNode" or negate:
                    continue
                for c in self.src.get(fnode.find("CardId").get("Id"), ()):
                    idnode, _ = self.port[c]
                    ds = idnode.find("DS")
                    if idnode.get("Type") == "GetCardIdNode" and ds is not None and self.side(ds.get("Id")) == {SELF}:
                        return True
        return False

    def template_ref(self, port_el, owner_id, _seen=frozenset(), copies=None):
        """The template ids a definition or id port names: a set of ids, COPY
        when it is the template of a card chosen at run time, None when the
        value is not a template reference the walk can read. For COPY, the
        card ports it copies are appended to `copies`."""
        if port_el is None or port_el.get("Id") in _seen:
            return None
        seen = _seen | {port_el.get("Id")}
        sources = self.src.get(port_el.get("Id"))
        if not sources:
            ids = {int(e.get("TemplateId")) for e in port_el.iter() if e.get("TemplateId")}
            v = port_el.get("V")
            ids |= {int(v)} if v and v.isdigit() else set()
            return ids - {0} or None
        node, port = self.port[sources[0]]
        kind = node.get("Type")
        if kind == "SetVarNode":
            return self.template_ref(node.find("In"), owner_id, seen, copies)
        if kind in DEFINITION_PASSTHROUGH:
            refs = [
                self.template_ref(p, owner_id, seen, copies)
                for child in node
                for p in [child, *child]
                if p.get("Type") in ("CardDefinitionVar", "CardDefinitionListVar") and p is not port
            ]
            if any(r == COPY for r in refs):
                return COPY
            return set().union(*(r for r in refs if r)) or None
        if kind in ("GetCardsDefinitionsNode", "GetInitialDeckDefinitions"):
            return COPY
        if kind == "GetVarNode":
            writers = self.var_writers.get(node.get("VarId"))
            if writers:
                refs = [self.template_ref(self.port[w][1], owner_id, seen, copies) for w in writers]
                if any(r == COPY for r in refs):
                    return COPY
                ids = set().union(*(r for r in refs if r))
                return ids or None
            decl = self.var_decl.get(node.get("VarId"))
            if decl is None:
                return None
            ids = {int(e.get("TemplateId")) for e in decl.iter() if e.get("TemplateId")}
            if ids:
                return ids - {0} or None
            v = decl.get("V")
            return {int(v)} if v and v.isdigit() and int(v) else None
        if kind == "GetTemplateIdNode":  # the id of a definition
            return self.template_ref(node.find("DS"), owner_id, seen, copies)
        if kind in ("GetCardTemplateIdNode", "GetCardDefinitionNode"):  # of a card
            ds = node.find("DS")
            if ds is not None and self.side(ds.get("Id")) == {SELF}:
                return {owner_id}
            if copies is not None and ds is not None:
                copies.append(ds.get("Id"))
            return COPY
        return None

    def card_predicate(self, port_id, owner_id):
        """A predicate on templates for the cards a card port can hold, from
        the card queries and filters upstream; None when unconstrained."""
        options = []
        for src, filters in self.origins(port_id):
            if src.get("Type") != "GetCardsNode":
                return None
            preds = [
                lambda t, ty=self.literal(src, "CardTypes", UNIT_TYPE), ti=self.literal(src, "CardTiers", 15): bool(
                    t.type & ty and t.tier & ti
                )
            ]
            for slot_id, keep in filters:
                if slot_id is None:
                    continue
                pred, _ = self.filter_predicate(slot_id, owner_id)
                if pred is not None:
                    preds.append(pred if keep else (lambda t, p=pred: not p(t)))
            options.append(lambda t, ps=preds: all(p(t) for p in ps))
        return (lambda t, os=options: any(o(t) for o in os)) if options else None

    def int_value(self, port_el):
        """A constant int on a port (a literal or an unwritten variable), else None."""
        if port_el is None:
            return None
        sources = self.src.get(port_el.get("Id"))
        if not sources:
            v = port_el.get("V")
            return int(v) if v and v.lstrip("-").isdigit() else None
        node, _ = self.port[sources[0]]
        if node.get("Type") != "GetVarNode" or self.var_writers.get(node.get("VarId")):
            return None
        decl = self.var_decl.get(node.get("VarId"))
        v = decl.get("V") if decl is not None else None
        return int(v) if v and v.lstrip("-").isdigit() else None

    def filter_predicate(self, slot_id, owner_id):
        """A filter port as (predicate on a Template or None, notes).

        Notes: `narrow` (the filter picks a category, ids or a property, not
        only a tier or type mask), `copy` (copies of a card chosen at run
        time), `dynamic` (compares with a run-time value),
        `unmodelled:<node type>`."""
        sources = self.src.get(slot_id, ())
        if not sources:
            return None, set()
        fnode, _ = self.port[sources[0]]
        kind = fnode.get("Type")
        negate = fnode.find("Negate") is not None and fnode.find("Negate").get("V") == "True"
        lit = lambda name: self.literal(fnode, name)
        pred, notes = None, {"narrow"}
        if kind == "FilterCardCategoryNode":
            el = fnode.find("CardCategory/V")
            mask = tuple(int(e.get("V")) for e in el) if el is not None else ()
            has_all = lit("CompareMethod") == 0
            if any(mask):
                pred = lambda t, m=mask: (
                    all(c & w == w for c, w in zip(t.categories, m))
                    if has_all
                    else any(c & w for c, w in zip(t.categories, m))
                )
            else:
                notes = {"unmodelled:" + kind}
        elif kind in ("FilterDefinitionNode", "FilterCardTemplateIdNode", "FilterDefinitionListNode"):
            name = {"FilterDefinitionNode": "Definition", "FilterCardTemplateIdNode": "TemplateId"}.get(kind, "Definitions")
            copies = []
            ref = self.template_ref(fnode.find(name), owner_id, copies=copies)
            if ref == COPY:
                # A copy of a card chosen in play: the deck card must pass the
                # filters that chose the original.
                notes = {"copy"}
                preds = [self.card_predicate(c, owner_id) for c in copies]
                if preds and all(p is not None for p in preds):
                    pred = lambda t, ps=preds: any(p(t) for p in ps)
            elif ref:
                pred = lambda t, ids=frozenset(ref): t.id in ids
                if negate:
                    notes = set()  # excluding named ids (usually itself) does not narrow
            else:
                notes = {"unmodelled:" + kind}
        elif kind == "FilterCardTierNode":
            pred, notes = (lambda t, m=lit("Tier"): bool(t.tier & m)), set()
        elif kind == "FilterCardTypeNode":
            pred, notes = (lambda t, m=lit("Type"): bool(t.type & m)), set()
        elif kind == "FilterCardFactionNode":
            pred = lambda t, m=lit("Faction"): bool(t.faction & m)
        elif kind == "FilterCardTemplateTokensNode":
            pred = lambda t, m=lit("Tokens"): t.tokens & m == m
        elif kind == "FilterCardPlacementNode":
            side, rows = lit("PlayerSide") or 3, lit("Location") or ACTIVE
            pred = lambda t, s=side, r=rows: bool(
                (s & 1 and t.own_side & r) or (s & 2 and t.opp_side & r)
            )
            notes = {"narrow"} if side == 2 else set()  # "lands on the other side": spies
        elif kind in ("FilterHasOnKilledAbilityNode", "FilterHasOnPlayedAbilityNode"):
            attr = "deathwish" if kind == "FilterHasOnKilledAbilityNode" else "deploy"
            pred = lambda t, a=attr: getattr(t, a)
        elif kind == "FilterCardPowerNode":
            value, op = self.int_value(fnode.find("Power")), lit("Op")
            if value is None or op is None:
                notes = {"dynamic"}
            else:
                compare = [
                    lambda a, b: a == b,
                    lambda a, b: a != b,
                    lambda a, b: a < b,
                    lambda a, b: a > b,
                    lambda a, b: a <= b,
                    lambda a, b: a >= b,
                ][op]
                pred = lambda t, f=compare, v=value: f(t.power, v)
        elif kind == "FilterCardLogicNode":
            parts = [self.filter_predicate(s.get("Id"), owner_id) for s in _children(fnode, "Ins")]
            notes = set().union(*(n for _, n in parts)) if parts else set()
            preds = [p for p, _ in parts if p is not None]
            if preds and len(preds) == len(parts):
                combine = any if lit("Op") == 1 else all
                pred = lambda t, ps=preds, c=combine: c(p(t) for p in ps)
            elif preds and lit("Op") != 1:
                pred = lambda t, ps=preds: all(p(t) for p in ps)
        else:
            notes = {"unmodelled:" + kind}
        if pred is not None and negate:
            pred = lambda t, p=pred: not p(t)
        return pred, notes

    # ------------------------------------------------------------ flow

    def flow_reach(self, start, through_conditions=True):
        """Node ids the flow from `start` reaches (optionally stopping at
        condition nodes)."""
        seen, stack = set(), [start]
        while stack:
            node = stack.pop()
            if node.get("Id") in seen:
                continue
            seen.add(node.get("Id"))
            if not through_conditions and node is not start and node.get("Type") in CONDITIONS:
                continue
            for child in node:
                if child.get("Type") == "FlowConnector" and child.tag != "FlowIn":
                    stack += [self.port[d][0] for d in self.dst.get(child.get("Id"), ())]
        return seen

    def data_deps(self, node):
        """What a node's data inputs read, transitively: ("type", node type),
        ("port", node type, port name) for each source port, ("var", id) for
        each variable read, and ("timer",) for the card's own timer."""
        out, seen, stack = set(), set(), [node]
        while stack:
            n = stack.pop()
            if n.get("Id") in seen:
                continue
            seen.add(n.get("Id"))
            kind = n.get("Type")
            out.add(("type", kind))
            if kind == "GetVarNode":
                out.add(("var", n.get("VarId")))
            if kind == "GetCardTimerNode" and n.find("DS") is not None and self.side(n.find("DS").get("Id")) == {SELF}:
                out.add(("timer",))
            if kind == "GetLocationsNode" and self.literal(n, "TokenMask"):
                out.add(("type", "RowTokenQuery"))
            if kind == "GetCardsNode":
                loc = self.literal(n, "LocationId") or 0
                if loc & (DECK | HAND | GRAVEYARD):
                    out.add(("type", "CardsOffBoard"))
            for child in n:
                for p in [child, *child]:
                    if not p.get("Id") or p.get("Type") == "FlowConnector":
                        continue
                    for s in self.src.get(p.get("Id"), ()):
                        src_node, src_port = self.port[s]
                        out.add(("port", src_node.get("Type"), src_port.tag))
                        stack.append(src_node)
        return out

    def written(self, nid):
        """The state a node writes: ("var", id) or ("timer",) for the own timer."""
        node = self.nodes[nid]
        if node.get("Type") == "SetVarNode":
            return {("var", node.get("VarId"))}
        if node.get("Type") == "ChangeCardTimerNode" and self.side(node.find("Card").get("Id")) == {SELF}:
            return {("timer",)}
        return set()

    def writes(self, reach):
        """Variables and the own timer that the nodes in `reach` write."""
        return set().union(set(), *(self.written(nid) for nid in reach))

    def _latched(self, trigger, reach, others):
        """True when the trigger fires once: a condition on its path reads
        state that the flow after the condition writes (a "first time only"
        variable or the own timer), and no repeating board trigger resets it.
        A variable written before the condition is a temporary, not a latch."""
        for c in reach:
            if self.nodes[c].get("Type") not in CONDITIONS:
                continue
            read = {d for d in self.data_deps(self.nodes[c]) if d[0] in ("var", "timer")}
            downstream = self.flow_reach(self.nodes[c])
            after = self.writes(downstream - {c})
            before = self.writes(reach - downstream)
            # Other repeating board triggers that write the state elsewhere reset it.
            rearm = set().union(set(), *(self.writes(r - downstream) for r in others))
            if (read & after) - before - rearm:
                return True
        return False

    def triggers(self, template):
        """Every trigger of the ability: {id: Trigger}."""
        nodes = [n for n in self.nodes.values() if is_trigger(n.get("Type"))]
        reach = {n.get("Id"): self.flow_reach(n) for n in nodes}
        # Where the card's timer runs: where the triggers that tick it fire.
        timer_locs = {
            self.literal(n, "OwnerLocation", ACTIVE)
            for n in nodes
            if n.get("Type") not in ("PlayedTrigger", "TimerTriggered", "TimerChanged")
            and n.find("OwnerLocation") is not None
            and ("timer",) in self.writes(reach[n.get("Id")])
        } or ({ACTIVE} if template.timer >= 0 else set())
        out = {}
        for n in nodes:
            kind, r = n.get("Type"), reach[n.get("Id")]
            if kind in ("TimerTriggered", "TimerChanged"):
                locs = frozenset(timer_locs)
            elif n.find("OwnerLocation") is not None:
                locs = frozenset({self.literal(n, "OwnerLocation", ACTIVE)})
            else:
                locs = frozenset()
            written = self.writes(r)
            others = [
                reach[m.get("Id")]
                for m in nodes
                if m is not n
                and m.get("Type") not in ONE_SHOT
                and (m.find("OwnerLocation") is None or self.literal(m, "OwnerLocation", ACTIVE) & ACTIVE)
            ]
            latch = self._latched(n, r, others)
            if kind in ONE_SHOT or (n.find("IsAmbushing") is not None and n.find("IsAmbushing").get("V") == "True"):
                repeats = False
            elif kind == "TimerTriggered":
                repeats = ("timer",) in written  # re-arms its own timer
            else:
                repeats = not latch
            out[n.get("Id")] = Trigger(n.get("Id"), kind, locs, repeats, r, self.flow_reach(n, through_conditions=False))
        return out

    def condition_class(self, condition):
        """`modal` (a choose-one), `state` (deck shape, score, passing, how it
        was played, row tokens) or `local` (a loop, a target check, a full row)."""
        deps = self.data_deps(condition)
        types = {d[1] for d in deps if d[0] == "type"}
        if "RequestTemplateChoiceNode" in types:
            return "modal"
        played_from = {"PlayedTrigger", "GetCardLocationIdNode"} <= types  # e.g. resurrected
        if types & STATE_SOURCES or ("port", "PlayedTrigger", "From") in deps or played_from:
            return "state"
        return "local"


@dataclass
class Trigger:
    id: str
    kind: str
    locs: frozenset  # where the card must be for it to fire (OwnerLocation)
    repeats: bool  # fires again and again while the card stays there
    reach: set  # node ids its flow reaches
    open_reach: set  # node ids its flow reaches without passing a condition


# ---------------------------------------------------------------- derivation


def effect_kind(graph, node):
    kind = node.get("Type")
    if kind == "ChangePowerNode":
        op = graph.literal(node, "Operation")
        return {0: "boost", 1: "damage", 2: "set", 3: "multiply", 4: "heal"}.get(
            op, "power?"
        )
    if kind == "DestroyCardsNode":
        return "consume" if graph.literal(node, "RemovalType") == 1 else "destroy"
    if kind == "ChangeTokensNode":
        op, tokens = (
            graph.literal(node, "Operation"),
            graph.literal(node, "Tokens") or 0,
        )
        if op in (0, 2, 3) and tokens & LOCK:
            return "lock"
        if op == 1 and tokens & AMBUSH:
            return "flip"
        if op in (0, 2, 3) and tokens & RESILIENCE:
            return "resilience"
        if op in (0, 2) and tokens & REVEALED:
            return "reveal"
        return "tokens"
    return {
        "BanishCardsNode": "banish",
        "ResetCardsNode": "reset",
        "TransformCardsNode": "transform",
        "ChangeArmorNode": "armor",
        "MoveCardsNode": "move",
        "PlayCardsNode": "play",
        "SummonCardsNode": "summon",
        "SpawnCardNode": "spawn",
        "DrawCardsNode": "draw",
        "ChangeLocationTokensNode": "weather",
        "ChangeCardTimerNode": "timer",
        "ForceCardTriggerNode": "retrigger",
    }.get(kind)


def target_port(node):
    for name in (
        "Targets",
        "Cards",
        "CardsToPlay",
        "Locations",
        "Location",
        "Position",
        "PlayerId",
        "Card",
    ):
        if node.find(name) is not None:
            return node.find(name)
    return None


def position_location(graph, port_el):
    """The location mask a position port lands in, when constant."""
    if port_el is None:
        return None
    for s in graph.upstream(port_el.get("Id")):
        node, _ = graph.port[s]
        if node.get("Type") in (
            "MakeCardPositionNode",
            "GetRandomLastAvailablePositionNode",
            "GetRandomAvailablePositionNode",
        ):
            if graph.connected(node.find("Location")):
                return None  # a row chosen at run time: on the board
            return graph.literal(node, "Location")
        if node.get("Type") in ("SetVarNode", "GetVarNode"):
            inner = node.find("In") if node.get("Type") == "SetVarNode" else None
            got = position_location(graph, inner) if inner is not None else None
            if got is None:
                for w in graph.var_writers.get(node.get("VarId"), ()):
                    got = position_location(graph, graph.port[w][1]) or got
            if got is not None:
                return got
        if node.get("Type") == "GetCardPositionNode":
            return ACTIVE
    return None


def facts_of(template, graph):
    """Every effect node of a card's graph with its sides, triggers and, for
    card-moving effects, where the cards come from."""
    swap = {OWN: OPP, OPP: OWN}
    # A spy's graph runs from the side it lands on: swap back to the player's.
    fix = (lambda sides: frozenset(swap.get(s, s) for s in sides)) if template.spy else (lambda s: s)
    triggers = graph.triggers(template)
    conditions = {
        nid: graph.condition_class(node) for nid, node in graph.nodes.items() if node.get("Type") in CONDITIONS
    }
    effects = []
    for node in graph.nodes.values():
        kind = effect_kind(graph, node)
        if kind is None:
            continue
        nid = node.get("Id")
        port = target_port(node)
        sides = fix(graph.side(port.get("Id"))) if port is not None else frozenset({UNKNOWN})
        trigs = [t for t in triggers.values() if nid in t.reach]
        gated = bool(trigs) and all(nid not in t.open_reach for t in trigs)
        # The classes of the conditions that sit between a deploy and the effect.
        gates = {
            conditions[c]
            for t in trigs
            if t.kind == "PlayedTrigger"
            for c in t.reach & set(conditions)
            if nid in graph.flow_reach(graph.nodes[c])
        }
        effect = dict(node=nid, kind=kind, sides=sides, triggers=trigs, gated=gated, gates=gates, origins=[])
        if port is not None and port.get("Type") in CARD_TYPES:
            for src, filters in graph.origins(port.get("Id")):
                deck_read = src.get("Type") == "GetCardsNode"
                effect["origins"].append(
                    dict(
                        kind=src.get("Type"),
                        loc=graph.literal(src, "LocationId") if deck_read else None,
                        side=fix(graph.side(src.find("PlayerId").get("Id"))) if deck_read else frozenset(),
                        types=graph.literal(src, "CardTypes", UNIT_TYPE) if deck_read else None,
                        tiers=graph.literal(src, "CardTiers", 15) if deck_read else None,
                        filters=filters,
                    )
                )
        if kind == "move":
            position = node.find("Position")
            effect["to"] = position_location(graph, position)
            effect["to_side"] = fix(graph.side(position.get("Id"))) if graph.connected(position) else frozenset()
        if kind == "spawn":
            ref = graph.template_ref(node.find("Definition"), template.id)
            effect["spawns_other"] = ref != {template.id}
            effect["spawned"] = ref - {template.id} if isinstance(ref, set) else set()
        effects.append(effect)
    return effects


@dataclass
class Pull:
    """What a play, summon or move from the owner's deck can reach. Each deck
    read is scoped on its own: named (ids), category (a category or property
    filter), copy (copies of a card chosen in play) or broad (tier/type only)."""

    targets: set = field(default_factory=set)
    specific: set = field(default_factory=set)  # targets of named and category reads
    named: set = field(default_factory=set)
    scopes: set = field(default_factory=set)
    notes: set = field(default_factory=set)


def own_deck(origin):
    return origin["kind"] == "GetCardsNode" and (origin["loc"] or 0) & DECK and OWN in origin["side"]


def analyse_pull(template, effect, pool, graph):
    pull = Pull()
    for origin in filter(own_deck, effect["origins"]):
        preds, named, notes = [], set(), set()
        for slot_id, keep in origin["filters"]:
            if slot_id is None:
                notes.add(keep)  # "level": the highest or lowest of the candidates
                continue
            pred, n = graph.filter_predicate(slot_id, template.id)
            notes |= n if keep else n - {"narrow"}
            if pred is not None:
                preds.append(pred if keep else (lambda t, p=pred: not p(t)))
            if keep:
                named |= positive_ids(graph, slot_id, template.id)
        targets = {
            t.id
            for t in pool.values()
            # A Neutral card's deck holds one faction, whichever it is.
            if (template.faction == NEUTRAL or t.faction in (template.faction, NEUTRAL))
            and t.type & origin["types"]
            and t.tier & origin["tiers"]
            and all(p(t) for p in preds)
        }
        scope = (
            "copy" if "copy" in notes
            else "named" if named
            else "category" if "narrow" in notes
            else "highest" if "level" in notes
            else "broad"
        )
        pull.targets |= targets
        if scope in ("named", "category"):
            pull.specific |= targets
        pull.named |= named
        pull.scopes.add(scope)
        pull.notes |= notes
    return pull


def positive_ids(graph, slot_id, owner_id):
    """Template ids a filter keeps by name (through OR/AND logic nodes)."""
    ids = set()
    for s in graph.src.get(slot_id, ()):
        fnode, _ = graph.port[s]
        kind = fnode.get("Type")
        if fnode.find("Negate") is not None and fnode.find("Negate").get("V") == "True":
            continue
        if kind == "FilterCardLogicNode":
            for slot in _children(fnode, "Ins"):
                ids |= positive_ids(graph, slot.get("Id"), owner_id)
        elif kind in ("FilterDefinitionNode", "FilterCardTemplateIdNode", "FilterDefinitionListNode"):
            name = {"FilterDefinitionNode": "Definition", "FilterCardTemplateIdNode": "TemplateId"}.get(kind, "Definitions")
            ref = graph.template_ref(fnode.find(name), owner_id)
            if ref and ref != COPY:
                ids |= ref
    return ids


@dataclass
class Card:
    template: Template
    roles: set = field(default_factory=set)
    flags: set = field(default_factory=set)
    rules: set = field(default_factory=set)
    uncertain: list = field(default_factory=list)
    tutor_targets: set = field(default_factory=set)
    named_targets: set = field(default_factory=set)
    tutor_scope: set = field(default_factory=set)
    specific_targets: set = field(default_factory=set)
    pulled_by: set = field(default_factory=set)
    spawned: set = field(default_factory=set)
    prerequisites: list = field(default_factory=list)
    payoffs: list = field(default_factory=list)
    interactions: list = field(default_factory=list)
    modes: dict = field(default_factory=dict)
    trigger_prerequisites: set = field(default_factory=set)
    prerequisite_numbers: dict = field(default_factory=dict)  # client data: split off before writing

    def add(self, role, rule):
        self.roles.add(role)
        self.rules.add(rule)


SELF_SOURCES = {"GetAbilityOwnerCardNode", "GetAbilityOwnerCardListNode"}


def derive_card(template, graph, pool):
    """Roles of one card from its graph; `pool` is the collectible templates."""
    card = Card(template)
    if template.spy:
        card.add("spy", "placement: opponent side only")
    if template.tokens & RESILIENCE:
        card.add("carryover", "template token: Resilience")
    if template.tokens & AMBUSH:
        card.flags.add("ambush")
        card.rules.add("template token: Ambush (face down, scores nothing until it flips)")
    if graph is None:
        card.add("body", "no card ability")
        return card
    unresolved, buffs = False, set()
    effects = facts_of(template, graph)
    every_mode, some_mode = set(), set()
    for e in effects:
        before = set(card.roles)
        _effect_roles(card, template, graph, pool, e, buffs)
        added = card.roles - before
        (some_mode if "modal" in e["gates"] else every_mode).update(added)
        unresolved |= e["kind"] in HARMFUL and UNKNOWN in e["sides"] and OPP not in e["sides"]
    # A deck buff on the card a tutor just played is the tutor's own boost.
    if "hand" in buffs or ("deck" in buffs and "tutor" not in card.roles):
        card.add("handbuff", "boosts cards in its owner's " + " and ".join(sorted(buffs)))
    if unresolved and not card.roles & {"removal", "enabler"}:
        card.uncertain.append("a harmful effect's side depends on run-time state")
    if "ambush" in card.flags:
        card.trigger_prerequisites |= {
            f"on_{event_name(graph, graph.nodes[t.id])}" for e in effects if e["kind"] == "flip" for t in e["triggers"]
        }
    card.prerequisites = sorted(set(prerequisites(graph, card.prerequisite_numbers)) | card.trigger_prerequisites)
    card.payoffs, card.interactions = structure_of(template, graph, effects)
    if "modal" in card.flags:
        options = [graph.template_ref(n.find("ValidChoices"), template.id) for n in graph.of_type("RequestTemplateChoiceNode")]
        card.modes = dict(
            options=max((len(o) for o in options if isinstance(o, set)), default=None),
            roles_in_every_mode=sorted(every_mode),
            roles_in_some_mode=sorted(some_mode - every_mode),
        )
    return card


def _effect_roles(card, template, graph, pool, e, buffs):
    """The roles, flags and rules one effect gives its card."""
    kind, sides, trigs = e["kind"], e["sides"], e["triggers"]
    kinds = {t.kind for t in trigs}
    locs = set().union(*(t.locs for t in trigs)) if trigs else set()
    on_board = not locs or any(loc & ACTIVE for loc in locs)
    off_board = lambda mask: any(loc & mask for loc in locs) and not on_board
    board = lambda t: not t.locs or any(loc & ACTIVE for loc in t.locs)
    deploy = "PlayedTrigger" in kinds
    if kind in HARMFUL and OPP in sides:
        card.add("removal", f"{kind} reaches opponent cards")
    if kind in ("damage", "destroy") and OWN in sides and OPP not in sides:
        card.add("enabler", f"{kind} on its own other units")
    if kind == "consume" and OWN in sides:
        card.add("enabler", "consumes its own units (board, hand or graveyard)")
    if kind == "consume" and OPP in sides and any((o["loc"] or 0) & ACTIVE for o in e["origins"]):
        card.add("removal", "consumes opponent units on the board")
    if kind == "retrigger" and OWN in sides:
        card.add("enabler", "fires an ally's ability again")
    in_hand = e["origins"] and all(
        o["kind"] == "GetCardsNode" and (o["loc"] or 0) & HAND and not (o["loc"] or 0) & ACTIVE for o in e["origins"]
    )
    if deploy and not in_hand and (
        kind in ("boost", "armor", "heal") and sides & {OWN, SELF, UNKNOWN}
        or kind in ("set", "multiply") and sides == {SELF}
    ):
        card.add("points", f"deploy {kind} on its own side")
    if kind == "boost":
        for o in e["origins"]:
            loc = o["loc"] or 0 if o["kind"] == "GetCardsNode" else 0
            if loc & (HAND | DECK):
                buffs.add("hand" if loc & HAND else "deck")
    if kind == "reveal":
        card.add("reveal", "reveals cards in a hand")
    if kind == "resilience" and sides & {OWN, SELF}:
        card.add("carryover", "adds Resilience")
    self_harm = kind in HARMFUL and sides <= {SELF}
    repeating = sorted({t.kind for t in trigs if t.repeats and board(t)})
    if kind in ENGINE_EFFECTS and not self_harm and repeating:
        card.add("engine", f"{kind} on {'/'.join(repeating)}, again and again")
    elif (kind in ENGINE_EFFECTS | HARMFUL | {"move"}) and not self_harm and not repeating:
        if any(t.kind == "TimerTriggered" and board(t) for t in trigs):
            card.add("timer", f"{kind} once when its timer on the board runs out")
    if kinds & ROUND_TRIGGERS and on_board:
        card.add("carryover", "round trigger on the board")
    if kinds & {"KilledTrigger", "BeforeDestroyedTrigger"}:
        card.add("deathwish", "acts when destroyed")
    if kind == "weather":
        card.add("weather", "changes row tokens on the " + ("opponent's" if OPP in sides else "own") + " side")
    if kind == "move" and OPP in sides and OWN in e["to_side"] and OPP not in e["to_side"] \
            and (e["to"] or ACTIVE) & ACTIVE:
        card.add("removal", "moves opponent cards to its own side (seize)")
    if kind == "move" and OPP in sides and e["to"] is not None and not e["to"] & ACTIVE \
            and e["to"] & (HAND | DECK):
        card.add("removal", "returns opponent cards to a hand or deck")
    if kind == "move" and e["to"] is not None and e["to"] & GRAVEYARD and not e["to"] & ACTIVE and any(
        own_deck(o) or (o["kind"] == "GetCardsNode" and (o["loc"] or 0) & HAND and OWN in o["side"])
        for o in e["origins"]
    ):
        card.add("discard", "moves its owner's deck or hand cards to the graveyard")
    if kind == "draw" and sides & {OWN, SELF, UNKNOWN}:
        card.add("draw", "draws")
    if kind == "spawn" and e["spawns_other"]:
        card.add("create", "spawns other templates")
        card.spawned |= e["spawned"]
    elif kind == "spawn" and deploy:
        card.add("points", "deploy spawns copies of itself")
    if deploy and e["gated"] and kind in GATED_EFFECTS:
        if "state" in e["gates"]:
            card.flags.add("conditional")
            card.rules.add(f"deploy {kind} depends on the game state")
        if "modal" in e["gates"]:
            card.flags.add("modal")
            card.rules.add(f"deploy {kind} is one option of a choice")
    if kind not in ("move", "play", "summon"):
        return
    if e["origins"] and all(o["kind"] in SELF_SOURCES for o in e["origins"]):
        events = sorted({event_name(graph, graph.nodes[t.id]) for t in trigs})
        if off_board(GRAVEYARD):
            card.flags.add("comes_back")
            card.rules.add("a graveyard trigger plays or moves the card itself")
            # From a round or turn trigger in the graveyard: back in any round.
            if kinds & (ROUND_TRIGGERS | TURN_TRIGGERS):
                card.add("carryover", "comes back by itself in a later round")
        elif off_board(DECK | HAND):
            card.flags.add("self_summon")
            card.rules.add("a deck or hand trigger plays or summons the card itself")
        card.trigger_prerequisites |= {f"on_{x}" for x in events}
        return
    if any(o["kind"] == "GetCardsNode" and (o["loc"] or 0) & GRAVEYARD for o in e["origins"]):
        card.add("revive", f"{kind} from a graveyard")
    if not any(map(own_deck, e["origins"])):
        return
    if kind == "move" and e["to"] is not None and not e["to"] & ACTIVE:
        if e["to"] & HAND:
            card.add("draw", "moves deck cards to a hand")
        return
    pull = analyse_pull(template, e, pool, graph)
    card.tutor_scope |= pull.scopes
    card.tutor_targets |= pull.targets - {template.id}
    card.specific_targets |= pull.specific - {template.id}
    card.named_targets |= pull.named - {template.id}
    if pull.named == {template.id} and off_board(DECK | HAND):
        card.flags.add("self_summon")
        card.rules.add("a deck trigger plays or summons a copy of itself")
        card.trigger_prerequisites |= {f"on_{event_name(graph, graph.nodes[t.id])}" for t in trigs}
        return
    if template.id in pull.named or pull.targets == {template.id}:
        card.add("thin_copy", f"{kind} copies of itself from the deck")
    scope = "/".join(sorted(pull.scopes))
    if pull.targets - {template.id} or "copy" in pull.scopes:
        card.add("tutor", f"{kind} from its own deck ({scope})")
    elif not pull.targets:
        card.add("tutor", f"{kind} from its own deck ({scope})")
        card.uncertain.append("tutor filter matches no collectible card")
    missing = sorted(n.split(":")[1] for n in pull.notes if n.startswith("unmodelled:"))
    if missing and pull.scopes != {"named"}:
        card.uncertain.append("tutor filter not modelled: " + ", ".join(missing))


def derive(templates, graphs, token_graphs, pool):
    """Roles for every template in `pool` ({id: Template}); returns {id: Card}."""
    collectible = {
        i: t for i, t in templates.items() if t.availability == clientdata.BASE_SET
    }
    cards = {
        tid: derive_card(templates[tid], graphs.get(tid), collectible) for tid in pool
    }
    named_by_weather = set()
    for g in token_graphs:
        for node in g.nodes.values():
            for el in node.iter():
                if el.get("TemplateId") and int(el.get("TemplateId")) in templates:
                    named_by_weather.add(int(el.get("TemplateId")))
        for decl in g.var_decl.values():
            inner = decl.find("V")
            if (
                inner is not None
                and inner.get("TemplateId")
                and int(inner.get("TemplateId")) in templates
            ):
                named_by_weather.add(int(inner.get("TemplateId")))
    # One level: a card that spawns known templates does what they do.
    # A pool of more than SPAWN_POOL templates (a random bronze of a
    # category) says little; from a small pool a role counts when one option
    # has it (the player picks, or the card's text names the options).
    for card in cards.values():
        options = [derive_card(templates[i], graphs.get(i), collectible) for i in sorted(card.spawned) if i in templates]
        if not options or len(options) > SPAWN_POOL:
            continue
        for role in sorted(PROPAGATED):
            having = [o.template.id for o in options if role in o.roles]
            if having:
                card.add(role, f"spawns {role} cards ({', '.join(map(str, having))})")
                if card.modes and role not in card.modes["roles_in_every_mode"]:
                    card.modes["roles_in_some_mode"] = sorted(set(card.modes["roles_in_some_mode"]) | {role})
    for tid in named_by_weather & set(cards):
        cards[tid].roles.discard("body")
        cards[tid].roles.add("weather")
        cards[tid].rules.add("a weather token ability names it")
    for tid, card in cards.items():
        # Broad pulls (any bronze, any unit) say nothing about one card.
        for target in card.specific_targets & set(cards):
            cards[target].pulled_by.add(tid)
        for target in card.named_targets:
            if target in cards:
                cards[target].roles.add("target")
                cards[target].rules.add(f"named by the deck pull of {tid}")
    names = Counter(c.template.name for c in cards.values())
    for card in cards.values():
        t = card.template
        if not card.roles:
            card.uncertain.append(
                "only the flag " + ", ".join(sorted(card.flags)) + " was derived" if card.flags else "no role derived"
            )
        if names[t.name] > 1:
            card.uncertain.append("another template in the set has the same name")
    return cards


# ---------------------------------------------------------------- output


# ---------------------------------------------------------------- structure
#
# Card-level mechanics beside the roles: what must hold for an effect
# (prerequisites), what the effects do (payoffs) and how other cards' actions
# trigger this one (interactions). Deck-level context (does this deck satisfy
# a prerequisite?) lives in a separate file and never edits these lists.

ZONE_BITS = ((ACTIVE, "board"), (HAND, "hand"), (DECK, "deck"), (GRAVEYARD, "graveyard"))
GAME_STATES = {8: "round_start", 0x10: "turn_start", 0x20: "turn", 0x40: "turn_end", 0x80: "round_end"}
EVENT_NAMES = {
    "AfterPlayedTrigger": "card_played",
    "BeforePlayedTrigger": "card_played",
    "AfterSpawnedTrigger": "unit_spawned",
    "AfterChangePowerTrigger": "power_changed",
    "AfterChangedLocationTokenTrigger": "row_token_changed",
    "AfterChangedCardTokenTrigger": "card_token_changed",
    "AfterPassedTrigger": "player_passed",
    "AfterKilledTrigger": "unit_killed",
    "KilledTrigger": "destroyed",
    "BeforeDestroyedTrigger": "destroyed",
    "AfterTransformedTrigger": "unit_transformed",
    "AfterMulliganTrigger": "mulligan",
    "BeforeTurnTrigger": "turn_start",
    "AfterTurnTrigger": "turn_end",
    "BeforeTurnEndTrigger": "turn_end",
    "BeforeRoundTrigger": "round_start",
    "AfterRoundTrigger": "round_end",
    "TimerTriggered": "timer",
    "TimerChanged": "timer_changed",
}
POSITIVE_SELF = {"boost", "transform", "play", "move", "summon", "spawn", "armor", "heal"}


def zone_name(mask):
    return "/".join(name for bit, name in ZONE_BITS if mask & bit) or "other"


def tier_words(mask):
    return "/".join(TIER[b] for b in (2, 4, 8, 1) if mask & b) if mask and mask & 15 != 15 else "any tier"


def deck_definition_filter(graph, node):
    """A starting-deck query (GetInitialDeckDefinitions) as (predicate on
    templates, short description)."""
    tier, kind, faction = (graph.literal(node, n, 0) or 0 for n in ("Tier", "Type", "Faction"))
    cat = node.find("Category/V")
    mask = tuple(int(e.get("V")) for e in cat) if cat is not None else ()
    has_all = graph.literal(node, "CategoryCompare") == 0

    def pred(t):
        if tier and not t.tier & tier or kind and not t.type & kind or faction and not t.faction & faction:
            return False
        if any(mask):
            hits = [c & w for c, w in zip(t.categories, mask)]
            return all(h == w for h, w in zip(hits, mask)) if has_all else any(hits)
        return True

    return pred, tier_words(tier) + (" in a category" if any(mask) else "")


def data_nodes(graph, node):
    """The node and every node its data inputs come from, transitively."""
    out, seen, stack = [], set(), [node]
    while stack:
        n = stack.pop()
        if n.get("Id") in seen:
            continue
        seen.add(n.get("Id"))
        out.append(n)
        for child in n:
            for p in [child, *child]:
                if p.get("Id") and p.get("Type") != "FlowConnector":
                    stack += [graph.port[s][0] for s in graph.src.get(p.get("Id"), ())]
    return out


def prerequisites(graph, numbers=None):
    """Mechanical prerequisites of a card's effects: [(tag, evaluate)], where
    evaluate(deck) -> a value for a deck {template: copies, ...} with
    `templates`, or None when the deck alone cannot answer it. A tag names
    its threshold `N`; the value is client data and goes into `numbers`
    ({tag: value}), never into roles.json."""
    out = {}
    numbers = {} if numbers is None else numbers
    for cond in graph.of_type(*CONDITIONS):
        if graph.condition_class(cond) != "state":
            continue
        nodes = data_nodes(graph, cond)
        kinds = {n.get("Type"): n for n in nodes}
        if cond.get("Type") == "HasNCopiesOfDefinitions":
            src = next((n for n in data_nodes(graph, cond) if n.get("Type") == "GetInitialDeckDefinitions"), None)
            count = graph.literal(cond, "Count")
            if src is not None:
                pred, desc = deck_definition_filter(graph, src)
                tag = f"starting_deck_copies=N ({desc})"
                numbers[tag] = count
                out[tag] = (
                    lambda deck, tpl, p=pred, n=count: all(c == n for i, c in deck.items() if i in tpl and p(tpl[i]))
                )
            continue
        if "GetInitialDeckDefinitions" in kinds:
            pred, desc = deck_definition_filter(graph, kinds["GetInitialDeckDefinitions"])
            out[f"starting_deck_count ({desc})"] = (
                lambda deck, tpl, p=pred: sum(c for i, c in deck.items() if i in tpl and p(tpl[i]))
            )
        if "PlayerGetScoreNode" in kinds:
            limits = [
                graph.int_value(n.find("B"))
                for n in nodes
                if n.get("Type") in ("IntCompareNode", "IntBranchNode") and graph.int_value(n.find("B"))
            ]
            if limits:
                numbers["score_difference>N"] = limits[0]
            out["score_difference>N" if limits else "score_comparison"] = None
        if "PlayerGetHasPassedNode" in kinds:
            out["a_player_has_passed"] = None
        row_query = any(n.get("Type") == "GetLocationsNode" and graph.literal(n, "TokenMask") for n in nodes)
        if row_query or kinds.keys() & {"GetLocationTokenOfType", "GetLocationToken", "GetLocationsByTokenTypeNode"}:
            out["row_token"] = None
        if "LocationIdContains" in kinds and "PlayedTrigger" in kinds:
            zone = graph.literal(kinds["LocationIdContains"], "B", 0)
            out[f"played_from={zone_name(zone)}"] = None
        if kinds.keys() & {"GetMatchingPlayedCardsNode", "GetCardsPlayedInCurrentTurnNode"}:
            out["cards_played_this_turn"] = None
        if "BranchGameStateNode" in kinds:
            state = graph.literal(kinds["BranchGameStateNode"], "GameState", 0)
            out[f"game_state_check={GAME_STATES.get(state, state)}"] = None
        for n in nodes:
            if n.get("Type") == "GetCardsNode" and (graph.literal(n, "LocationId") or 0) & (DECK | HAND | GRAVEYARD):
                out[f"cards_in_{zone_name(graph.literal(n, 'LocationId'))}"] = None
    return out


def target_word(sides):
    if sides == {SELF}:
        return "self"
    if sides and sides <= {OWN, SELF}:
        return "own"
    if sides == {OPP}:
        return "opp"
    return "any"


def event_name(graph, trigger):
    """A trigger as a short event name ("discarded", "swapped", ...)."""
    kind = trigger.get("Type")
    if kind in ("AfterMovedTrigger", "BeforeMovedTrigger"):
        src, dst = graph.literal(trigger, "FromLocation", 0) or 0, graph.literal(trigger, "ToLocation", 0) or 0
        if src & (HAND | DECK) and dst == GRAVEYARD:
            return "discarded"
        if src & HAND and dst & DECK:
            return "swapped"
        if src & ACTIVE and dst == GRAVEYARD:
            return "destroyed_or_moved_to_graveyard"
        return f"moved_{zone_name(src)}_to_{zone_name(dst)}"
    if kind in ("AfterDestroyedTrigger", "AfterBanishedTrigger"):
        return "unit_consumed" if graph.literal(trigger, "RemovalType") == 1 else "unit_destroyed"
    return EVENT_NAMES.get(kind, kind)


def structure_of(template, graph, effects):
    """(payoffs, interactions) of one card from its effects."""
    payoffs, interactions = set(), set()
    for e in effects:
        if e["kind"] in ("timer", "tokens"):
            continue  # bookkeeping, not a payoff
        target = target_word(e["sides"])
        payoff = f"{e['kind']}_{target}"
        payoffs.add(payoff)
        for t in e["triggers"]:
            if t.kind == "PlayedTrigger":
                continue
            event = event_name(graph, graph.nodes[t.id])
            interactions.add(f"{event}:{payoff}")
            positive = target == "self" and e["kind"] in POSITIVE_SELF
            if event == "power_changed" and positive:
                interactions.add("benefits_from_damage")
            if event == "power_changed" and e["kind"] == "damage" and target == "self":
                interactions.add("hurt_by_damage")
            named = {"discarded": "discard", "swapped": "swap", "unit_consumed": "consume"}.get(event)
            if named and (positive or e["kind"] == "boost"):
                interactions.add(f"benefits_from_{named}")
    return sorted(payoffs), sorted(interactions)


def deck_context(templates, graphs, deck):
    """Deck-level context for one deck {template: copies}: for each card with
    a prerequisite the deck list can answer, {tag: value}. The card's own
    prerequisites stay as they are; this only says whether this deck meets them."""
    out = {}
    for tid in sorted(deck):
        graph = graphs.get(tid)
        if graph is None:
            continue
        answers = {
            tag: evaluate(deck, templates)
            for tag, evaluate in prerequisites(graph).items()
            if evaluate is not None
        }
        if answers:
            out[str(tid)] = answers
    return out


def roster_context(client, directory=ROSTER):
    """deck-context.json: per roster deck, the prerequisites its
    list answers (copies, starting-deck counts)."""
    templates, graphs, _ = client
    decks = {}
    for path in sorted(directory.glob("*.json")):
        deck = json.loads(path.read_text())
        counts = Counter({deck["leader"]["id"]: 1})
        for c in deck["cards"]:
            counts[c["id"]] += c.get("count", 1)
        context = deck_context(templates, graphs, dict(counts))
        if context:
            decks[path.stem] = context
    return {
        "source": "scripts/card_roles.py --deck-context: deck-level context only; a card's prerequisites in roles.json stay",
        "decks": decks,
    }


def removal_priority(roles, flags):
    """How much killing this unit on the opponent's board is worth beyond its
    power: (rank, answer). Rank 2: an engine or a running timer; rank 1: a
    card that comes back (answer with banish or lock, or ignore); rank 0: the
    rest. Tutors and enablers have spent their effect once on the board: they
    count in hand value and belief, not here."""
    if "engine" in roles or "timer" in roles:
        rank = 2
    elif "comes_back" in flags:
        rank = 1
    else:
        rank = 0
    return dict(rank=rank, answer="banish_or_lock" if "comes_back" in flags else "any")


def deck_links(cards, deck, powers=None):
    """{tutor id: [its targets inside this deck]} for one deck (a list of
    template ids), from roles.json's `cards`. A `highest` pull resolves to the
    highest-power candidates when `powers` ({id: base power}, joined from the
    client) is given. A card in the deck that some tutor in the deck can pull
    is a weak draw while that tutor is still in the deck."""
    ids = {int(i) for i in deck}
    links = {}
    for tid in sorted(ids):
        card = cards.get(str(tid))
        if not card or "tutor" not in card["roles"]:
            continue
        inside = set(card["tutor_targets"]) & ids
        if card["tutor_scope"] == ["highest"] and powers and inside:
            top = max(powers[i] for i in inside)
            inside = {i for i in inside if powers[i] == top}
        if inside:
            links[tid] = sorted(inside)
    return links


def roster_copies(directory=ROSTER):
    copies = Counter()
    names = {}
    for path in sorted(directory.glob("*.json")):
        deck = json.loads(path.read_text())
        copies[deck["leader"]["id"]] += 1
        names[deck["leader"]["id"]] = deck["leader"]["name"]
        for c in deck["cards"]:
            copies[c["id"]] += c.get("count", 1)
            names[c["id"]] = c["name"]
    return copies, names


def merge_human(entry, human):
    """Final roles and flags of one card after a human override."""
    roles, flags = set(entry["roles"]), set(entry["flags"])
    if "roles" in human:
        roles = set(human["roles"])
    roles = (roles | set(human.get("add", ()))) - set(human.get("remove", ()))
    for flag, on in human.get("flags", {}).items():
        flags = flags | {flag} if on else flags - {flag}
    return sorted(roles), sorted(flags)


def record(card, copies, names, human):
    t = card.template
    entry = dict(
        id=t.id,
        name=names.get(t.id, t.name),
        roles=sorted(card.roles),
        flags=sorted(card.flags),
        tutor_scope=sorted(card.tutor_scope),
        tutor_targets=sorted(card.tutor_targets),
        named_targets=sorted(card.named_targets),
        pulled_by=sorted(card.pulled_by),
        prerequisites=card.prerequisites,
        prerequisite_numbers=card.prerequisite_numbers,
        payoffs=card.payoffs,
        interactions=card.interactions,
        confidence="low" if card.uncertain else "high",
        uncertain=card.uncertain,
        rules=sorted(card.rules),
    )
    if card.modes:
        entry["modes"] = card.modes
    entry["roster_copies"] = copies.get(t.id, 0)
    override = human.get(str(t.id))
    if override:
        entry["derived_roles"], entry["derived_flags"] = entry["roles"], entry["flags"]
        entry["roles"], entry["flags"] = merge_human(entry, override)
        entry["human"] = override
        entry["confidence"] = "human"
    entry["review_status"] = (
        "human" if override and override.get("by") == "human"
        else "needs_review" if entry["confidence"] == "low"
        else "derived"
    )
    entry["removal_priority"] = removal_priority(entry["roles"], entry["flags"])
    return entry


SCHEMA = {
    "version": 3,
    "source": "scripts/card_roles.py: the structure of the client's ability graphs (no card text) plus data/cards/roles-human.json",
    "roles": ROLES,
    "flags": FLAGS,
    "card": {
        "id": "template id",
        "name": "project name (roster deck files), else the client's English card name",
        "(no stats)": "tier, power and faction are client data and are not in this file: join them from the client at run time (card_roles.load_client)",
        "roles": "final roles: derived, then the human override",
        "flags": "final flags: derived, then the human override",
        "tutor_scope": "named (ids) | category (a category or property filter) | copy (copies of a card chosen in play, filtered as the original) | highest (the highest or lowest of the deck's candidates: resolve per deck) | broad (tier/type only)",
        "tutor_targets": "collectible templates of its faction or Neutral that its deck pull can reach (any faction for a Neutral card)",
        "named_targets": "templates its deck pull names by id (these get the role `target`)",
        "pulled_by": "templates whose named or category deck pull can reach this card",
        "confidence": "high | low (see `uncertain`) | human (an override exists)",
        "prerequisites": "mechanical prerequisites of its effects (a condition in the graph), e.g. starting_deck_copies=N (bronze), score_difference>N, played_from=graveyard; N is client data, kept in .local/cards/answer-params/<roles sha>.json; deck context never removes one (see deck-context.json)",
        "payoffs": "what its effects do: <effect>_<self|own|opp|any>",
        "interactions": "how other cards' actions fire it: <event>:<payoff>, and benefits_from_damage/discard/swap/consume, hurt_by_damage",
        "modes": "choose-one cards: roles in every mode and in some mode (the card's roles are the union); the number of options is client data (.local)",
        "review_status": "derived | needs_review (low confidence) | human (a human label applied)",
        "uncertain": "why the derivation may be wrong",
        "rules": "which derivation rules fired",
        "roster_copies": "copies in the roster decks",
        "removal_priority": "rank 2 engine or timer, 1 comes_back, 0 other; answer banish_or_lock for comes_back, else any (on the opponent's board, beyond power)",
        "derived_roles, derived_flags, human": "present when a human override exists",
    },
}


def build(client, roster_only=False, human_path=HUMAN):
    """roles.json content from `load_client()`'s result."""
    templates, graphs, token_graphs = client
    copies, names = roster_copies()
    if roster_only:
        pool = [t for t in copies if t in templates]
    else:
        pool = [
            t for t, tpl in templates.items() if tpl.availability == clientdata.BASE_SET
        ]
    cards = derive(templates, graphs, token_graphs, pool)
    human = json.loads(human_path.read_text()) if human_path.exists() else {}
    data = {
        "schema": SCHEMA,
        "scope": "roster" if roster_only else "collectible",
        "cards": {
            str(t): record(cards[t], copies, names, human)
            for t in sorted(cards)
        },
    }
    import answer_kinds  # imports this module: import here, not at the top

    answer_kinds.annotate(data, client, cards)
    return data


def coverage(data):
    cards = data["cards"].values()
    count = Counter()
    for c in cards:
        count["templates"] += 1
        count["with a role (not body)"] += bool(set(c["roles"]) - {"body"})
        count["no role"] += not c["roles"]
        count["low confidence"] += c["confidence"] == "low"
        for r in c["roles"]:
            count["role:" + r] += 1
        for f in c["flags"]:
            count["flag:" + f] += 1
    return count


REVIEW_COLUMNS = [
    "Id", "Card", "Faction", "Tier", "Copies", "Derived roles (flags)",
    "Mechanics (graph)", "Why uncertain", "Suggested", "Why", "Human label",
]


def review_candidates(data):
    """Roster cards a human should label: low confidence, most-played first."""
    cards = sorted(data["cards"].values(), key=lambda c: (-c["roster_copies"], c["name"]))
    return [c for c in cards if c["roster_copies"] and c["confidence"] == "low"]


def derived_cell(card):
    roles = card.get("derived_roles", card["roles"])
    flags = card.get("derived_flags", card["flags"])
    return ", ".join(roles + [f"({f})" for f in flags]) or "none"


def mechanics_cell(card):
    parts = card.get("prerequisites", []) + [i for i in card.get("interactions", []) if ":" not in i]
    return ", ".join(f"`{p}`" for p in parts)


def parse_review(text):
    """The review sheet as (lines before the table, header, rows, lines after);
    each row is {column: cell}."""
    lines = text.split("\n")
    start = next(i for i, line in enumerate(lines) if line.startswith("| Id |"))
    end = start + 2
    while end < len(lines) and lines[end].startswith("|"):
        end += 1
    header = [c.strip() for c in lines[start].strip("|").split("|")]
    rows = [dict(zip(header, (c.strip() for c in line.strip().strip("|").split("|")))) for line in lines[start + 2:end]]
    return lines[:start], header, rows, lines[end:]


def update_review(data, path, templates, extra=()):
    """Refresh the review sheet in place: the derived column of every row
    follows roles.json; rows for new low-confidence cards (and `extra`
    {id: (suggested, why)}) are appended. Suggestions and human labels are
    kept as they are."""
    extra = dict(extra)
    if path.exists():
        before, header, rows, after = parse_review(path.read_text())
    else:
        before = ["# Card roles: review sheet", ""]
        header, rows, after = REVIEW_COLUMNS, [], [""]
    header = list(REVIEW_COLUMNS)  # the canonical columns; cells move by name
    listed = {r["Id"] for r in rows}
    new = [c for c in review_candidates(data) if str(c["id"]) not in listed]
    new += [
        data["cards"][str(i)]
        for i in extra
        if str(i) not in listed and str(i) in data["cards"] and i not in {c["id"] for c in new}
    ]
    for card in new:
        suggested, why = extra.get(card["id"], ("", ""))
        t = templates[card["id"]]
        rows.append({
            "Id": str(card["id"]), "Card": card["name"], "Faction": FACTION.get(t.faction, str(t.faction)),
            "Tier": TIER.get(t.tier, str(t.tier)), "Copies": str(card["roster_copies"]),
            "Why uncertain": "; ".join(card["uncertain"]) or "a reviewer's hand label",
            "Suggested": suggested, "Why": why, "Human label": "",
        })
        listed.add(str(card["id"]))
    for row in rows:
        card = data["cards"].get(row["Id"])
        if card:
            row["Derived roles (flags)"] = derived_cell(card)
            row["Mechanics (graph)"] = mechanics_cell(card)
    labels = "**Allowed labels:** " + ", ".join(f"`{x}`" for x in REVIEW_LABELS) + "."
    before = [labels if line.startswith("**Allowed labels:**") else line for line in before]
    line = lambda cells: "|" + "|".join(f" {c} " if c else " " for c in cells) + "|"
    table = [line(header), "|" + "---|" * len(header)]
    table += [line(row.get(c, "") for c in header) for row in rows]
    path.write_text("\n".join(before + table + after))
    return rows


def import_review(path, human_path):
    """The label sheet's overrides into roles-human.json.
    `ok` keeps the derived set; any other label set replaces the derived roles
    and flags (a flag not named is switched off); `none` means no role."""
    _, _, rows, _ = parse_review(path.read_text())
    human = json.loads(human_path.read_text()) if human_path.exists() else {}
    imported = 0
    for row in rows:
        cell = row.get("Human label", "").replace("`", "").strip()
        if not cell:
            continue
        labels = {x.strip() for x in cell.split(",") if x.strip()}
        unknown = labels - set(REVIEW_LABELS)
        if unknown:
            raise ValueError(f"{row['Id']} {row['Card']}: unknown labels {sorted(unknown)}")
        entry = {"name": row["Card"], "by": "human"}
        if labels != {"ok"}:
            entry["roles"] = sorted(labels & set(ROLES))
            entry["flags"] = {f: f in labels for f in sorted(FLAGS)}
        human[row["Id"]] = entry
        imported += 1
    human_path.write_text(json.dumps(human, indent=1, ensure_ascii=False) + "\n")
    return imported


KEY_ROLES = {"engine", "tutor", "removal", "enabler", "spy", "revive", "carryover", "timer"}


def verdict(derived, label):
    """A sample card's verdict: right, partly, wrong (a false key role) or missing."""
    derived, label = set(derived), set(label)
    if derived == label:
        return "right"
    if not derived:
        return "missing"
    return "wrong" if (derived - label) & KEY_ROLES else "partly"


def evaluate(data, sample):
    """Verdict counts and per-role (tp, fp, fn) of the derived roles (before
    human overrides) against a labelled sample {id: {"roles": [...]}}."""
    counts, per_role, verdicts = Counter(), defaultdict(Counter), {}
    for tid, want in sample.items():
        card = data["cards"][tid]
        got = set(card.get("derived_roles", card["roles"]))
        verdicts[tid] = verdict(got, want["roles"])
        counts[verdicts[tid]] += 1
        for role in got | set(want["roles"]):
            per_role[role]["tp" if role in got and role in want["roles"] else "fp" if role in got else "fn"] += 1
    return counts, per_role, verdicts


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument(
        "--roster",
        action="store_true",
        help="only the cards of the roster decks",
    )
    parser.add_argument("--out", type=Path, default=OUT)
    parser.add_argument("--human", type=Path, default=HUMAN)
    parser.add_argument(
        "--review", type=Path, help="also write a label sheet here (optional local label overrides)"
    )
    parser.add_argument("--import-review", type=Path, help="read optional local label overrides from a label sheet into --human, then derive")
    parser.add_argument("--check", type=Path, help="score the derived roles against a labelled sample (JSON file; no default)")
    parser.add_argument("--deck-context", type=Path, default=DECK_CONTEXT,
                        help="per roster deck: which card prerequisites the deck list meets")
    args = parser.parse_args(argv)
    if args.import_review:
        print("imported labels:", import_review(args.import_review, args.human))
    client = load_client()
    data = build(client, roster_only=args.roster, human_path=args.human)
    import answer_kinds

    data, params = answer_kinds.split(data)  # numbers stay local
    if args.out == OUT:
        answer_kinds.write_params(params, data)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(answer_kinds.roles_text(data))
    for key, n in sorted(coverage(data).items()):
        print(f"{key:28} {n}")
    if args.check:
        counts, per_role, verdicts = evaluate(data, json.loads(args.check.read_text())["cards"])
        print("sample:", dict(counts))
        for role, c in sorted(per_role.items()):
            print(f"  {role:12} tp {c['tp']:2} fp {c['fp']:2} fn {c['fn']:2}")
        for tid, v in verdicts.items():
            if v != "right":
                print(f"  {v:8} {tid} {data['cards'][tid]['name']}")
    if args.review:
        print("review rows:", len(update_review(data, args.review, client[0])))
    if args.deck_context:
        context = roster_context(client)
        args.deck_context.write_text(json.dumps(context, indent=1) + "\n")
        print("deck context:", len(context["decks"]), "decks")


if __name__ == "__main__":
    sys.exit(main())
