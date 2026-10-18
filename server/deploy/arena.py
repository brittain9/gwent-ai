"""Arena runs for the local server.

The client's arena flow (GOGArenaDataRetriever, ArenaService) is a quest of
type `arena` (quest definition 1000000) with two `finish_game` objectives
(wins and losses), a quest deck drafted one step at a time through
`/users/{id}/quest_decks/{deck}/building`, and a free ticket item that the
client consumes to start a run. This module holds the run state and builds
the client's DTOs; server.py routes the requests here.

One JSON file per user, `arena_{uid}.json` in the server data directory:
{"runs": [run, ...]}. The last run whose state is `active` is the current run.

Draft: 26 card steps, then one leader step. Every step offers cards of one
tier; the 26 card tiers are a shuffled schedule of 9 gold, 8 silver and
9 bronze, so every drafted deck has that mix whatever the player picks. Cards
are drawn without replacement inside a run, from every faction. The client's
own randomization data (GwentUnity.Tests.ArenaDeckGenerator reads a
ScriptableObject) is not in the client's code, so these numbers are this server's
choice. Docs: server/docs/arena.md.
"""

import json
import os
import random
import threading
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ARENA_QUEST_DEFINITION_ID = 1000000  # QuestDefinitionExtensions.ArenaRun
CARD_STEPS = 26
CARD_CHOICES = 4
LEADER_CHOICES = 4
TOTAL_STEPS = CARD_STEPS + 1
TIER_COUNTS = {"gold": 9, "silver": 8, "bronze": 9}  # sums to CARD_STEPS
WINS_TO_COMPLETE = 9
LOSSES_TO_FAIL = 3

# Ticket: one free inventory item of category ArenaTicket, and a shop product
# of category ArenaTickets (the multiplayer menu shows the arena tile only
# while the shop lists one: AppMainMenuMultiplayerState.CreateArenaPane). nginx
# serves the shop list statically: the product is in scripts/server_static.py.
TICKET_ITEM_ID = 8000001
TICKET_DEFINITION_ID = 8000
TICKET_PRODUCT_ID = "8000000000000001"

# Templates.xml <Tier>: 1 leader, 2 bronze, 4 silver, 8 gold (ECardTier).
TIER_NAMES = {1: "leader", 2: "bronze", 4: "silver", 8: "gold"}

_locks = {}
_locks_guard = threading.Lock()


def _lock(uid):
    with _locks_guard:
        return _locks.setdefault(str(uid), threading.Lock())


def _now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S+0000")


def full_id(template_id):
    """Card definition id the client uses: template id * 100 (+1 premium)."""
    return int(template_id) * 100


# ── Card pool ───────────────────────────────────────────────────────────────


def load_tier_pool(templates_path):
    """{tier name: [template id, ...]} for the collectible base set."""
    with open(templates_path, "r", encoding="utf-8") as f:
        root = ET.fromstring("<root>" + f.read() + "</root>")
    pool = {name: [] for name in TIER_NAMES.values()}
    for tmpl in root.findall(".//Template[@Type='CardTemplate']"):
        if tmpl.get("Availability") != "1" or tmpl.findtext("Kind") != "1":
            continue
        name = TIER_NAMES.get(int(tmpl.findtext("Tier") or 0))
        if name:
            pool[name].append(int(tmpl.get("Id")))
    return pool


# ── Draft ───────────────────────────────────────────────────────────────────


def draft_offers(pool, rng=random):
    """All 27 steps' offers for one run: a list of lists of full ids."""
    schedule = [t for t, n in TIER_COUNTS.items() for _ in range(n)]
    rng.shuffle(schedule)
    drawn = {}
    for tier in TIER_COUNTS:
        need = schedule.count(tier) * CARD_CHOICES
        drawn[tier] = rng.sample(pool[tier], need)
    offers = []
    for tier in schedule:
        offers.append([full_id(t) for t in drawn[tier][:CARD_CHOICES]])
        drawn[tier] = drawn[tier][CARD_CHOICES:]
    offers.append([full_id(t) for t in rng.sample(pool["leader"], LEADER_CHOICES)])
    return offers


def new_run(pool, rng=random, min_id=0):
    """A fresh active run; ids are unique per user (above min_id)."""
    quest_id = max(int(time.time() * 1000), min_id)
    now = _now()
    return {
        "quest_id": quest_id,
        "deck_id": quest_id + 1,
        "state": "active",
        "date_created": now,
        "date_modified": now,
        "deck_created": False,
        "offers": draft_offers(pool, rng),
        "picks": [],
        "wins": 0,
        "losses": 0,
        "opponents": [],
    }


def draft_done(run):
    return len(run["picks"]) >= TOTAL_STEPS


def pick(run, step_id, card_definition_id):
    """Record one pick. Returns an error string, or None on success."""
    if run["state"] != "active":
        return "run is not active"
    if draft_done(run):
        return "draft is finished"
    step = len(run["picks"])
    if int(step_id) != step:
        return f"step_id {step_id} is not the current step {step}"
    if int(card_definition_id) not in run["offers"][step]:
        return f"card {card_definition_id} is not offered at step {step}"
    run["picks"].append(int(card_definition_id))
    run["date_modified"] = _now()
    return None


def record_result(run, won, opponent_name=""):
    """Count one finished arena match; close the run at 9 wins or 3 losses."""
    if run["state"] != "active":
        return "run is not active"
    if not draft_done(run):
        return "draft is not finished"
    if won:
        run["wins"] += 1
    else:
        run["losses"] += 1
    run["opponents"].append(
        {"username": opponent_name or "", "player_result": "win" if won else "lose"}
    )
    if run["wins"] >= WINS_TO_COMPLETE:
        run["state"] = "complete"
    elif run["losses"] >= LOSSES_TO_FAIL:
        run["state"] = "failed"
    run["date_modified"] = _now()
    return None


# ── Persistence ─────────────────────────────────────────────────────────────


class ArenaStore:
    """Per-user arena runs as JSON files in data_dir."""

    def __init__(self, data_dir, pool_loader):
        self.data_dir = data_dir
        self._pool_loader = pool_loader
        self._pool = None

    def pool(self):
        if self._pool is None:
            self._pool = self._pool_loader()
        return self._pool

    def _path(self, uid):
        return os.path.join(self.data_dir, f"arena_{int(uid)}.json")

    def load(self, uid):
        try:
            with open(self._path(uid), "r", encoding="utf-8") as f:
                return json.load(f)
        except FileNotFoundError:
            return {"runs": []}

    def save(self, uid, doc):
        path = self._path(uid)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        os.replace(tmp, path)

    @staticmethod
    def active(doc):
        for run in reversed(doc["runs"]):
            if run["state"] == "active":
                return run
        return None

    @staticmethod
    def by_quest(doc, quest_id):
        for run in doc["runs"]:
            if run["quest_id"] == int(quest_id):
                return run
        return None

    @staticmethod
    def by_deck(doc, deck_id):
        for run in doc["runs"]:
            if run["deck_id"] == int(deck_id):
                return run
        return None


# ── Client DTOs (GwentWebServiceClient.DTO / .DTO.Quest) ────────────────────


def _objective(oid, result, value):
    return {
        "id": oid,
        "required": True,
        "type": "finish_game",
        "value": value,
        "global": False,
        "filters": {"player_results": [result]},
    }


def quest_definition():
    return {
        "group_id": 0,
        "id": ARENA_QUEST_DEFINITION_ID,
        "name": "Arena",
        "date_from": "2018-01-01T00:00:00+0000",
        "date_to": None,
        "type": "arena",
        "objective_definitions": [
            _objective(1, "win", WINS_TO_COMPLETE),
            _objective(2, "lose", LOSSES_TO_FAIL),
        ],
    }


def user_quest(run, uid):
    def obj(oid, result, value, current):
        return {
            "current_value": current,
            "current_percentage": min(100, current * 100 // value),
            "quest_objective_definition": _objective(oid, result, value),
        }

    return {
        "id": run["quest_id"],
        "user_id": int(uid),
        "state": run["state"],
        "date_created": run["date_created"],
        "date_modified": run["date_modified"],
        "quest_definition": {"id": ARENA_QUEST_DEFINITION_ID, "type": "arena"},
        "user_quest_objectives": [
            obj(1, "win", WINS_TO_COMPLETE, run["wins"]),
            obj(2, "lose", LOSSES_TO_FAIL, run["losses"]),
        ],
        "global_quest_objectives": [],
    }


def quest_deck(run):
    return {
        "id": run["deck_id"],
        "type": "arena",
        "state": "ready" if draft_done(run) else "deck_building",
        "user_quest_id": run["quest_id"],
        "card_definitions": [{"id": c} for c in run["picks"]],
    }


def building_status(run):
    """ActionsCollection<QuestDeckBuildingStatus>; actions null when done
    (the client reads a null or empty list as 'deck complete')."""
    if draft_done(run) or run["state"] != "active":
        return {"actions": None}
    step = len(run["picks"])
    return {
        "actions": [
            {
                "type": "select_card_definition",
                "current_step_id": step,
                "total_steps_count": TOTAL_STEPS,
                "current_step": {
                    "choice": [{"id": c} for c in run["offers"][step]],
                    "limit": 1,
                },
            }
        ]
    }


def collection(items):
    return {
        "items": items,
        "total_count": len(items),
        "limit": 100,
        "page_token": "",
        "next_page_token": None,
    }


def ticket_item(uid, consumed):
    return {
        "id": TICKET_ITEM_ID,
        "item_definition": {
            "id": TICKET_DEFINITION_ID,
            "item_template": {
                "id": TICKET_DEFINITION_ID,
                "name": "Arena Ticket",
                "category": "ArenaTicket",
                "consumable": True,
            },
            "parameters": {},
        },
        "state": "Consumed" if consumed else "New",
        "user_id": int(uid),
    }


def quest_updated_notification(run, uid):
    """Broker push that makes the client's QuestListener add or update the
    quest (QuestUpdatedPayload, NotificationType.quest_updated)."""
    return {
        "type": "quest_updated",
        "context": {"user_quest_id": run["quest_id"]},
        "payload": {"user_quest": user_quest(run, uid)},
    }


def deck_summary(run):
    """The drafted deck for the mod: leader and cards as full ids."""
    picks = run["picks"]
    leader = picks[CARD_STEPS] if len(picks) > CARD_STEPS else None
    return {"leader": leader, "cards": picks[:CARD_STEPS]}


def run_summary(run, uid):
    return {
        "user_id": int(uid),
        "user_quest_id": run["quest_id"],
        "quest_deck_id": run["deck_id"],
        "state": run["state"],
        "draft_step": len(run["picks"]),
        "total_steps": TOTAL_STEPS,
        "wins": run["wins"],
        "losses": run["losses"],
        "max_wins": WINS_TO_COMPLETE,
        "max_losses": LOSSES_TO_FAIL,
        "deck": deck_summary(run),
        "opponents": run["opponents"],
    }


# ── Request routing ─────────────────────────────────────────────────────────
# Each handler returns (status, json object or None, notification or None).
# server.py sends the JSON and forwards the notification to the broker.


def handle_client(store, method, path, query, body):
    """Client (GOG API) routes. Returns None when the path is not arena's."""
    import re

    m = re.match(r"^/users/(\d+)/(quests|quest_decks|items|opponents)(/.*)?$", path)
    if not m:
        return None
    uid, kind, rest = m.group(1), m.group(2), m.group(3) or ""
    with _lock(uid):
        doc = store.load(uid)
        run = store.active(doc)

        if kind == "quests":
            if method == "GET" and rest == "":
                qtype = query.get("type", "arena")
                runs = [run] if run and qtype == "arena" else []
                return 200, collection([user_quest(r, uid) for r in runs]), None
            mq = re.match(r"^/(\d+)$", rest)
            if method == "GET" and mq:
                found = store.by_quest(doc, mq.group(1))
                if not found:
                    return 404, {"error": "quest not found"}, None
                return 200, user_quest(found, uid), None
            ma = re.match(r"^/(\d+)/abandonment$", rest)
            if method == "POST" and ma:
                found = store.by_quest(doc, ma.group(1))
                if not found:
                    return 404, {"error": "quest not found"}, None
                if found["state"] == "active":
                    found["state"] = "abandoned"
                    found["date_modified"] = _now()
                    store.save(uid, doc)
                return 200, None, None
            return None

        if kind == "items":
            # Only the ticket's consumption is arena's; kegs stay in server.py.
            if method == "POST" and rest == f"/{TICKET_ITEM_ID}/consumption":
                if run is None:
                    last = max((r["deck_id"] for r in doc["runs"]), default=0)
                    run = new_run(store.pool(), min_id=last + 1)
                    doc["runs"].append(run)
                    store.save(uid, doc)
                return (
                    200,
                    {"user_quest_id": run["quest_id"]},
                    quest_updated_notification(run, uid),
                )
            return None

        if kind == "opponents":
            if method != "GET" or "arena" not in query.get("game_type", ""):
                return None
            page = int(query.get("page", "1") or 1)
            current = run or (doc["runs"][-1] if doc["runs"] else None)
            opps = []
            if current and page == 1:
                opps = [
                    {
                        "user_id": 0,
                        "username": o["username"],
                        "platform": "GOG",
                        "player_result": o["player_result"],
                    }
                    for o in reversed(current["opponents"])
                ]
            return 200, collection(opps), None

        # quest_decks
        if method == "POST" and rest == "":
            qid = int(body.get("user_quest_id", 0))
            found = store.by_quest(doc, qid)
            if not found:
                return 404, {"error": "quest not found"}, None
            found["deck_created"] = True
            store.save(uid, doc)
            return 200, quest_deck(found), None
        if method == "GET" and rest == "":
            qid = int(query.get("user_quest_id", 0))
            found = store.by_quest(doc, qid)
            decks = [quest_deck(found)] if found and found["deck_created"] else []
            return 200, collection(decks), None
        mb = re.match(r"^/(\d+)/building(/actions/select_card_definition)?$", rest)
        if mb:
            found = store.by_deck(doc, mb.group(1))
            if not found:
                return 404, {"error": "quest deck not found"}, None
            if method == "GET" and not mb.group(2):
                return 200, building_status(found), None
            if method == "POST" and mb.group(2):
                ids = (body.get("step_params") or {}).get("card_definition_ids") or []
                if len(ids) != 1:
                    return 400, {"error": "expected one card_definition_id"}, None
                err = pick(found, body.get("step_id", -1), ids[0])
                if err:
                    return 409, {"error": err}, None
                store.save(uid, doc)
                return 200, building_status(found), None
        return None


def handle_internal(store, method, path, body):
    """Mod routes on the internal port. Returns None when not arena's."""
    import re

    m = re.match(r"^/internal/arena/(\d+)(/result|/retire)?$", path)
    if not m:
        return None
    uid, action = m.group(1), m.group(2)
    with _lock(uid):
        doc = store.load(uid)
        run = store.active(doc)
        if method == "GET" and action is None:
            current = run or (doc["runs"][-1] if doc["runs"] else None)
            if current is None:
                return 404, {"error": "no arena run"}, None
            return 200, run_summary(current, uid), None
        if method != "POST" or action is None:
            return None
        if run is None:
            return 409, {"error": "no active arena run"}, None
        if action == "/result":
            err = record_result(
                run, bool(body.get("won")), str(body.get("opponent_name", ""))
            )
        else:
            run["state"] = "abandoned"
            run["date_modified"] = _now()
            err = None
        if err:
            return 409, {"error": err}, None
        store.save(uid, doc)
        return 200, run_summary(run, uid), quest_updated_notification(run, uid)
