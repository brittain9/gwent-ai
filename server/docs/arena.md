# Arena on the local server

The client's arena (Multiplayer, Arena tile) runs against this server up to
a drafted deck and an encounter screen. A run is a quest of type `arena`; the
deck is a quest deck drafted one pick at a time. The code is
`server/deploy/arena.py`; `server/deploy/server.py` routes to it.

## What the client does, step by step

| Step | Client code | Request | Server answer |
|---|---|---|---|
| Arena tile appears | `AppMainMenuMultiplayerState.CreateArenaPane` | `GET seawolf-shop /platforms/GOG/products` | nginx's static list (`scripts/server_static.py`) holds a product of category `ArenaTickets`, price 0 VIRTUAL |
| Login: quests | `QuestManager.Initialize` | `GET /quest_definitions`, `GET /users/{uid}/quests?state=active` | definition 1000000 (`ArenaRun`); the active run's `UserQuest`, if any |
| Login: inventory | `InventoryManager` | `GET /users/{uid}/items` | one item `8000001`, category `ArenaTicket`: `New` with no active run, `Consumed` during a run |
| Intro, use ticket | `AppArenaState.HandleTicketChosen` → `ConsumeTicket` | `POST /users/{uid}/items/8000001/consumption` `{"quest_definition_id":1000000}` | new run; `{"user_quest_id": Q}`; the broker pushes `quest_updated` with the `UserQuest`, which the client waits for (15 s) |
| Deck created | `ArenaService.CreateArenaDeck` | `POST /users/{uid}/quest_decks` `{"type":"arena","user_quest_id":Q}` | `QuestDeck` `{id: D, type: arena, state: deck_building, user_quest_id: Q, card_definitions: []}` |
| Deck looked up | `ArenaService.InitializeDeck` | `GET /users/{uid}/quest_decks?user_quest_id=Q` | the deck once created, else an empty list |
| Draft step | `GetQuestDeckBuildingStatus` | `GET /users/{uid}/quest_decks/D/building` | `{"actions":[{"type":"select_card_definition","current_step_id":n,"total_steps_count":27,"current_step":{"choice":[{"id":...}x4],"limit":1}}]}` |
| Pick | `ArenaService.SelectCard` | `POST /users/{uid}/quest_decks/D/building/actions/select_card_definition` `{"step_id":n,"step_params":{"card_definition_ids":[id]}}` | the next step, or `{"actions": null}` after the last pick (the client marks the deck `ready` and opens the encounter screen) |
| Encounter screen | `ArenaService.InitializeLastArenaOpponents` | `GET /users/{uid}/opponents?game_type=arena&page=p&limit=20&result=1&unique=0` | the run's recorded opponents, newest first, on page 1; page 2 and later are empty |
| After a match | `AppArenaState.TryProgressArenaAfterMatch` → `RedownloadQuest` | `GET /users/{uid}/quests/{Q}` | the run's `UserQuest` with current wins and losses |
| Abandon | `ArenaService.AbandonArena` | `POST /users/{uid}/quests/{Q}/abandonment` | state `abandoned`, 200 empty |

Card ids are full definition ids: template id × 100 (+1 premium; the draft
offers no premium cards). The leader pick is the last step; the client
recognises it because every choice is a leader.

## The draft

- 26 card steps, then 1 leader step: 27 picks, one card each.
- 4 choices per card step, 4 leaders in the leader step.
- Each card step offers one tier. The 26 card tiers are a shuffled schedule
  of 9 gold, 8 silver and 9 bronze, so every deck has that mix.
- Any faction, collectible base set only (`Templates.xml`: `Availability`
  1, `Kind` 1; 110 gold, 150 silver, 198 bronze, 21 leaders). No card is
  offered twice in one run.
- A run ends at 9 wins (`complete`) or 3 losses (`failed`).

Read from the client: the step and choice shapes, the leader-last order
(`GwentUnity.Tests.ArenaDeckGenerator` puts the leader set last), the arena
rule set (`ArenaDeckValidatorRuleSet`: no gold or silver totals, no faction
limit). Not in the client's code: the beta's randomization data (a Unity
asset), so 26 picks, 4 choices, the tier mix and 9 wins / 3 losses are this
server's choice; they are not verified against the beta's servers.

## Endpoints for the mod

The internal port (plain HTTP, `127.0.0.1:8444`, and `8447` on the bind
host; the same handler as `/internal/user_battle_deck/{uid}`).

| Request | Body | Answer |
|---|---|---|
| `GET /internal/arena/{uid}` | none | the active run, else the last run; 404 when the user has none |
| `POST /internal/arena/{uid}/result` | `{"won": true, "opponent_name": "GwentAI"}` | the updated run; 409 when no run is active or the draft is unfinished |
| `POST /internal/arena/{uid}/retire` | none | the run, state `abandoned`; 409 when no run is active |

The run object:

```json
{"user_id": 4242, "user_quest_id": 1792334966453, "quest_deck_id": 1792334966454,
 "state": "active", "draft_step": 27, "total_steps": 27,
 "wins": 1, "losses": 0, "max_wins": 9, "max_losses": 3,
 "deck": {"leader": 13110100, "cards": [15231100, "... 26 ids"]},
 "opponents": [{"username": "GwentAI", "player_result": "win"}]}
```

`state` is `active`, `complete`, `failed` or `abandoned`. The deck is also
in the client: `ArenaData.DeckData` (`GOGArenaDataRetriever.GetArenaData()`).

After a result or a retire the server asks the broker to push
`quest_updated` with the new `UserQuest`. The client's `QuestListener`
updates the quest, `GOGArenaDataRetriever.OnArenaQuestUpdated` updates the
lives and wins, and re-reads the opponents. If the push does not arrive,
`GOGArenaDataRetriever.TryForceRefreshCurrentQuest()` re-reads the quest
(`GET /users/{uid}/quests/{Q}`).

There is no client call that reports an arena match result: on GOG the
game service counted finished arena games into the quest. The mod's result
call stands in for that.

## Storage

`arena_{uid}.json` in the server data directory (`.runtime/server-data`):
`{"runs": [run, ...]}`, each run with its offers, picks, wins, losses and
opponents. Old runs stay in the file.

## Not built

- Rewards after a finished run (`quest_completed` notifications and a reward
  pack). The client's rewards screen waits for them.
- A second ticket in the same client session: the client removes the
  consumed ticket locally and reads the inventory again only at login.
  Restart the client to start the next run.
- Buying the ticket in the shop: it is free and already in the inventory;
  a shop purchase records no keg and adds nothing.
- Special arena events (definitions 1002001 to 1002012).
