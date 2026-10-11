using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using GwentAI.Search;
using GwentCore;
using GwentGameplay;

namespace GwentAI.Sim
{
    // A private copy of a live game, played forward without touching the
    // original. The copy's players are synchronous (RandomPlayers unless
    // given), so one Update() call runs as far as the rules allow.
    public class Playout
    {
        public readonly GameController Game;
        public EPlayerId Winner { get; private set; }
        public bool Ended { get; private set; }
        public int Updates { get; private set; }
        private bool m_Stopped;

        // Ends a run early, as a search does at its evaluation horizon.
        public void Stop()
        {
            m_Stopped = true;
        }

        private Playout(GameController game)
        {
            Game = game;
        }

        public static Playout Clone(GameController source, Random random)
        {
            return Clone(source, (players, id) => new RandomPlayer(players, id, random));
        }

        // A copy whose seats are the factory's synchronous players.
        public static Playout Clone(GameController source, Func<PlayerManager, EPlayerId, Player> seat)
        {
            long t0 = PhaseClock.Start();
            Playout playout = new Playout(source.Clone());
            PhaseClock.Stop(Phase.CloneEngine, t0);
            long t1 = PhaseClock.Start();
            GameController game = playout.Game;
            PlayerManager players = game.PlayerManager;
            for (int i = 0; i < players.Players.Length; i++)
            {
                Player cloned = players.Players[i];
                Player player = seat(players, cloned.Id);
                player.Copy(cloned, game);
                player.Restrictions.Copy(cloned.Restrictions, game);
                player.Restrictions.SetEnabledActions(AllActions);
                players.Players[i] = player;
            }
            PhaseClock.Stop(Phase.CloneSeats, t1);
            long t2 = PhaseClock.Start();
            // The copy inherits the original's pause requests but not the
            // TimeManager timers that release them (TimeManager.Copy copies
            // nothing), so a presentation delay such as the one after both
            // players pass would hold the copy forever. A copy presents
            // nothing; release them without calling back into the original's
            // requesters.
            foreach (PauseRequest pause in game.PauseRequests.ToArray())
            {
                game.PopPause(pause.Requester);
            }
            // The client's battle view closes this gate while it animates
            // and reopens it on the main game only; a copy made meanwhile
            // would never pass its next state change.
            game.CanAdvanceState = true;
            RepairCopy(source, game);
            playout.Game.EventManager.OnGameEnded.CoreEvent.AddListener(playout.OnGameEnded);
            PhaseClock.Stop(Phase.CloneRepair, t2);
            return playout;
        }

        // The client's UI switches the local (human) player's actions on and
        // off with the turn (BattleRestrictions.SetEnabledActions), and no UI
        // runs in a copy to switch them back on: a copy made during the AI's
        // turn left the human's seat unable to play or pass, so every
        // simulated game stalled at the human's next turn. Rules restrict
        // through ForceDisable, which the copy keeps.
        private static readonly EPlayerAction AllActions =
            Enum.GetValues(typeof(EPlayerAction)).Cast<EPlayerAction>().Aggregate((EPlayerAction)0, (all, action) => all | action);

        // Engine gaps in GameController.Copy, most of which the stock AI never
        // hits because it never copies a game while an ability waits on a
        // player:
        // - ActionManager.Copy shares the original's pool of action objects,
        //   which is not thread-safe; a copy simulated on another thread
        //   (Practice, the stock search) races the live game over it. Each
        //   copy gets its own pool.
        // - PlayStack.Copy copies nothing (it loops over its own, just
        //   cleared, list), losing cards queued to be played from the deck or
        //   graveyard.
        // - Requests are copied before the abilities, so a request raised by
        //   an ability (targets, choices, a mulligan effect) finds no ability
        //   instance to link to. Answering it then never resumes the ability,
        //   and the copy stalls. Each is linked to the copy's instance of its
        //   original's ability.
        // - RequestLocationTargetsAction does not copy its rows, so the copy
        //   offers no valid row. They are copied onto the copy's board.
        // - RequestMulliganAction does not copy its blacklist (templates
        //   already mulliganed, which may not be drawn again). It is copied;
        //   Belief clears the opponent's, which their seat cannot see.
        private static readonly FieldInfo s_ActionPool = AccessTools.Field(typeof(ActionManager), "m_ActionPool");
        private static readonly FieldInfo s_Blacklist = AccessTools.Field(typeof(RequestMulliganAction), "m_BlacklistedTemplates");

        private static void RepairCopy(GameController source, GameController copy)
        {
            s_ActionPool.SetValue(copy.ActionManager, new Dictionary<Type, Stack<AAction>>());
            copy.PlayStack.PlayStackEntries.Clear();
            copy.PlayStack.PlayStackEntries.AddRange(source.PlayStack.PlayStackEntries);
            BoardManager board = copy.CardManager.BoardManager;
            foreach (ARequestAction request in copy.RequestManager.Requests)
            {
                ARequestAction original = source.RequestManager.Requests.Find(other => other.Id == request.Id && other.PlayerId == request.PlayerId &&
                                                                                       other.TargetPlayer == request.TargetPlayer);
                if (original is RequestMulliganAction mulligan)
                {
                    ((HashSet<int>)s_Blacklist.GetValue(request)).UnionWith((HashSet<int>)s_Blacklist.GetValue(mulligan));
                }
                if (original is RequestLocationTargetsAction rows)
                {
                    RequestLocationTargetsAction copied = (RequestLocationTargetsAction)request;
                    copied.ValidTargets.Clear();
                    copied.ValidTargets.AddRange(rows.ValidTargets.Select(row => board.GetLocation(row.PlayerId, row.Loc)));
                    copied.SelectedTargets.Clear();
                    copied.SelectedTargets.AddRange(rows.SelectedTargets.Select(row => board.GetLocation(row.PlayerId, row.Loc)));
                }
                PropertyInfo property = request.GetType().GetProperty("AbilityInstance", BindingFlags.Public | BindingFlags.Instance);
                if (property == null)
                {
                    continue;
                }
                AbilityInstance ability = original == null ? null : (AbilityInstance)property.GetValue(original, null);
                if (ability != null)
                {
                    // The setter is private to the declaring base class.
                    property.DeclaringType.GetProperty(property.Name).GetSetMethod(true)
                        .Invoke(request, new object[] { copy.AbilityManager.Find(ability.Id) });
                }
            }
        }

        // Plays to the end of the game. Requests pending when the copy was
        // made were delivered to the original's players, so they are
        // re-delivered to the copy's own.
        public bool Run(int maxUpdates)
        {
            long start = PhaseClock.Start();
            try
            {
                return RunUpdates(maxUpdates);
            }
            finally
            {
                PhaseClock.Stop(Phase.Run, start);
            }
        }

        private bool RunUpdates(int maxUpdates)
        {
            ActionManager actions = Game.ActionManager;
            actions.BreakOnChange = false;
            foreach (ARequestAction request in Game.RequestManager.Requests.ToArray())
            {
                if (m_Stopped)
                {
                    break;
                }
                Game.PlayerManager.GetPlayer(request.PlayerId).OnRequestReceived(request);
            }
            while (!Ended && !m_Stopped && Updates < maxUpdates)
            {
                Game.Update();
                Updates++;
            }
            return Ended;
        }

        public static string Describe(GameController Game)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("state=").Append((EGameStateId)Game.StateMachine.GetCurrentStateID())
              .Append(" current=").Append(Game.PlayerManager.CurrentPlayerId)
              .Append(" round=").Append(Game.RoundManager.CurrentRoundIndex)
              .Append(" paused=").Append(Game.IsLogicPaused).Append('/').Append(Game.PauseRequests.Count)
              .Append(" actions=").Append(Game.ActionManager.HasActions)
              .Append(" abilities=").Append(Game.AbilityManager.HasInstances)
              .Append(" stacks=").Append(Game.ExecutionStack.HasCards).Append('/').Append(Game.PlayStack.HasCards)
              .Append(" dying=").Append(Game.CardManager.CardsWaitingToDie.Count)
              .Append(" dirty=").Append(Game.IsDirty)
              .Append(" advance=").Append(Game.CanAdvanceState)
              .Append(" main=").Append(Game.IsMainController)
              .Append(" authority=").Append(Game.HasAuthority)
              .Append(" mode=").Append(Game.GameInstance.GameMode)
              .Append(" network=").Append(Game.Network != null)
              .Append(" requests=");
            foreach (ARequestAction request in Game.RequestManager.Requests)
            {
                sb.Append(request.GetRequestType()).Append('/').Append(request.PlayerId).Append("->").Append(request.TargetPlayer)
                  .Append(request.Expired ? " expired" : "").Append(" #").Append(request.Id);
                if (request is RequestCardChoicesAction choices)
                {
                    sb.Append(" choices ").Append(choices.ValidChoices.Count).Append(" min ").Append(choices.MinChoices)
                      .Append(" max ").Append(choices.MaxChoices).Append(" selected ").Append(choices.SelectedChoices.Count);
                }
                sb.Append(',');
            }
            foreach (Player player in Game.PlayerManager.Players)
            {
                sb.Append(" [").Append(player.GetType().Name).Append(' ').Append(player).Append(']');
            }
            return sb.ToString();
        }

        // The score margin of the last finished round, from the seat's side.
        public int LastRoundMargin(EPlayerId seat)
        {
            int own = seat.ToIndex();
            int margin = 0;
            foreach (RoundInfo round in Game.RoundManager.Rounds)
            {
                if (round != null && round.Finished)
                {
                    margin = round.PlayerScores[own] - round.PlayerScores[1 - own];
                }
            }
            return margin;
        }

        // The score margin of the round with this index, from the seat's
        // side; null while it is not finished.
        public int? RoundMargin(EPlayerId seat, int index)
        {
            RoundInfo round = index < 0 ? null : Game.RoundManager.Rounds.ElementAtOrDefault(index);
            if (round == null || !round.Finished)
            {
                return null;
            }
            int own = seat.ToIndex();
            return round.PlayerScores[own] - round.PlayerScores[1 - own];
        }

        private void OnGameEnded(EPlayerId winner, EEndGameReason reason)
        {
            Winner = winner;
            Ended = true;
        }
    }
}
