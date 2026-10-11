---
name: Game report
about: A game where the AI did something odd, or played well
title: "Game report: "
labels: game report
---

**Your deck and the AI's deck** (faction and leader are enough):

**Where** (round and turn, or the card it played):

**What the AI did, and what you would have played**:

**The record** (optional, helps most): run `scripts/game-report.py --pack` and
attach the zip it writes. It holds the decks as card ids, every move, the
scores, the deal's seed, the AI's settings, search statistics and timings,
the start time in UTC and the number of search workers. It holds no deck
name you typed, no account and no path. Do not attach anything from the
game's own files.
