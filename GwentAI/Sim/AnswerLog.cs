using System;
using System.Collections.Generic;
using System.IO;
using GwentGameplay;
using HarmonyLib;
using Newtonsoft.Json;

namespace GwentAI.Sim
{
    // Writes what each attack on a unit did in the main game (destroy,
    // banish, damage and other power changes, reset, transform, tokens such
    // as lock, and moves off a side), with the targets before and after and
    // the board before, so answers can be measured after the game. Copies
    // made by searches are skipped. It is written beside the action
    // trace; Practice records collect it through `Logged`.
    public static class AnswerLog
    {
        private static TextWriter s_Writer;
        private static bool s_Installed;
        private static int s_Moves;
        private static readonly Stack<Pending> s_Pending = new Stack<Pending>();

        public static event Action<Dictionary<string, object>> Logged;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (s_Installed)
            {
                return;
            }
            s_Installed = true;
            MoveLog.Install(harmony);
            MoveLog.Applied += _ => s_Moves++;
            harmony.Patch(AccessTools.Method(typeof(ActionManager), nameof(ActionManager.ApplyAction)),
                prefix: new HarmonyMethod(typeof(AnswerLog), nameof(ApplyActionPrefix)),
                postfix: new HarmonyMethod(typeof(AnswerLog), nameof(ApplyActionPostfix)));
        }

        public static void Begin(string path)
        {
            End();
            s_Writer = new StreamWriter(path);
            Reset();
        }

        public static void End()
        {
            if (s_Writer != null)
            {
                s_Writer.Close();
                s_Writer = null;
            }
        }

        // A new game: move indexes count from 0 again.
        public static void Reset()
        {
            s_Moves = 0;
            s_Pending.Clear();
        }

        private sealed class Pending
        {
            public AAction Action;
            public Dictionary<string, object> Entry;
            public List<Card> Targets;
        }

        private static bool Listening => s_Writer != null || Logged != null;

        private static void ApplyActionPrefix(ActionManager __instance, AAction action)
        {
            if (!Listening || !__instance.GameController.IsMainController)
            {
                return;
            }
            GameController game = __instance.GameController;
            string kind;
            List<ushort> ids = Targets(action, out kind);
            if (ids == null)
            {
                return;
            }
            List<Card> targets = new List<Card>();
            foreach (ushort id in ids)
            {
                Card card = game.CardManager.GetCard(id);
                if (card != null && card.Position.IsActive)
                {
                    targets.Add(card);
                }
            }
            if (targets.Count == 0)
            {
                return;
            }
            Card by = action is ACardAction cardAction && cardAction.CardID != 0 ? game.CardManager.GetCard(cardAction.CardID) : null;
            Dictionary<string, object> entry = new Dictionary<string, object>
            {
                ["move"] = s_Moves - 1, // the last decision applied before this action
                ["round"] = game.RoundManager.CurrentRoundIndex + 1,
                ["action"] = kind,
                ["by"] = by == null ? null : new Dictionary<string, object>
                {
                    ["id"] = (int)by.Id,
                    ["t"] = by.Template.Id,
                    ["side"] = by.Position.PlayerId.ToString(),
                    ["zone"] = by.Position.Location.ToString(),
                },
                ["targets"] = targets.ConvertAll(card => Unit(card, true)),
                ["board"] = Board(game),
            };
            if (action is ChangePowerAction power)
            {
                entry["op"] = power.AttackInfo.Operation.ToString();
                entry["power_type"] = power.AttackInfo.PowerType.ToString();
            }
            else if (action is ChangeTokensAction tokens)
            {
                entry["op"] = string.Join(",", tokens.AttackInfo.Operations.ConvertAll(op => op.ToString()).ToArray());
                entry["tokens"] = string.Join(",", tokens.AttackInfo.Tokens.ConvertAll(token => token.ToString()).ToArray());
            }
            s_Pending.Push(new Pending { Action = action, Entry = entry, Targets = targets });
        }

        private static void ApplyActionPostfix(ActionManager __instance, AAction action)
        {
            if (s_Pending.Count == 0 || s_Pending.Peek().Action != action)
            {
                return;
            }
            Pending pending = s_Pending.Pop();
            pending.Entry["after"] = pending.Targets.ConvertAll(card => Unit(card, false));
            if (s_Writer != null)
            {
                s_Writer.WriteLine(JsonConvert.SerializeObject(pending.Entry));
            }
            Logged?.Invoke(pending.Entry);
        }

        // The attacks this log follows and their target ids, or null.
        private static List<ushort> Targets(AAction action, out string kind)
        {
            kind = null;
            switch (action)
            {
                case CardDestroyAttackAction a: kind = "destroy"; return a.TargetIDs;
                case CardBanishAttackAction a: kind = "banish"; return a.TargetIDs;
                case ChangePowerAction a: kind = "power"; return a.TargetIDs;
                case CardResetAttackAction a: kind = "reset"; return a.TargetIDs;
                case CardTransformAttackAction a: kind = "transform"; return a.TargetIDs;
                case ChangeTokensAction a: kind = "tokens"; return a.TargetIDs;
                case MoveAction a: kind = "move"; return a.CardIds;
                default: return null;
            }
        }

        // A unit's in-game state: where it is, its current power and lock.
        // No base power, tier or other template stats (CDPR data, left
        // out); after an attack, also whether it is dying.
        private static Dictionary<string, object> Unit(Card card, bool before)
        {
            Dictionary<string, object> unit = new Dictionary<string, object>
            {
                ["id"] = (int)card.Id,
                ["t"] = card.Template.Id,
                ["side"] = card.Position.PlayerId.ToString(),
                ["row"] = card.Position.Location.ToString(),
                ["p"] = card.Power,
                ["lock"] = (card.Data.Tokens.Tokens & ECardToken.Lock) != 0,
            };
            if (!before)
            {
                unit["dying"] = card.IsWaitingToDie;
            }
            return unit;
        }

        // Every unit on the board: [id, template, side, row, power].
        private static List<object[]> Board(GameController game)
        {
            List<object[]> board = new List<object[]>();
            foreach (Player player in game.PlayerManager.Players)
            {
                foreach (Card card in player.GetCardsInLocation(ELocation.Melee | ELocation.Ranged | ELocation.Siege))
                {
                    board.Add(new object[] { (int)card.Id, card.Template.Id, card.Position.PlayerId.ToString(), card.Position.Location.ToString(), card.IsAmbushWaitingToTrigger ? 0 : card.Power });
                }
            }
            return board;
        }
    }
}
