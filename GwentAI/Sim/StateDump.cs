using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GwentCore;
using GwentGameplay;

namespace GwentAI.Sim
{
    // The canonical state dump (ID.state's record body; the format is
    // OracleFormat's). It reads the
    // game and changes nothing: no event, no random draw, no allocation the
    // engine sees.
    public static class StateDump
    {
        private static readonly ELocation[] Rows = { ELocation.Melee, ELocation.Ranged, ELocation.Siege };

        // Copy == true leaves out what Playout.Clone sets on purpose in every
        // copy (each seat's EnabledActions): a clone check compares a copy
        // with its source on the rest.
        public static List<string> Lines(GameController game, bool copy = false)
        {
            List<string> lines = new List<string>();
            CardManager cards = game.CardManager;
            foreach (Card card in cards.AllCards)
            {
                if (card != null)
                {
                    lines.Add(CardLine(card));
                }
            }
            BoardManager board = cards.BoardManager;
            foreach (Player player in game.PlayerManager.Players)
            {
                foreach (Location location in board.GetBoardSide(player.Id).Locations)
                {
                    if (location != null)
                    {
                        lines.Add(OracleFormat.Zone((int)location.PlayerId, (int)location.Loc, location.Cards.Select(card => (int)card.Id).ToList()));
                    }
                }
            }
            foreach (Player player in game.PlayerManager.Players)
            {
                foreach (ELocation row in Rows)
                {
                    lines.Add(OracleFormat.RowToken((int)player.Id, (int)row, (int)board.GetLocation(player.Id, row).Token));
                }
            }
            foreach (Player player in game.PlayerManager.Players)
            {
                lines.Add(PlayerLine(player, copy));
            }
            RoundManager rounds = game.RoundManager;
            List<int[]> roundFields = new List<int[]>();
            foreach (RoundInfo round in rounds.Rounds)
            {
                if (round != null)
                {
                    roundFields.Add(new[] { round.Id, (int)round.StartingPlayerId, (int)round.WinnerId, round.TurnIndex, round.PlayerScores[0], round.PlayerScores[1] });
                }
            }
            lines.Add(OracleFormat.Round(rounds.CurrentRoundIndex, (int)game.PlayerManager.CurrentPlayerId, game.StateMachine.GetCurrentStateID(), roundFields));
            foreach (ARequestAction request in game.RequestManager.Requests)
            {
                AbilityInstance instance = RequestInstance(request);
                lines.Add(OracleFormat.Request(request.Id, (int)request.GetRequestType(), (int)request.PlayerId, (int)request.TargetPlayer,
                                               request.Expired, instance == null ? -1 : instance.Id));
            }
            List<AbilityInstance> instances = game.AbilityManager.ActiveInstances;
            int[] shares = OracleFormat.ShareGroups(instances.Select(instance => (object)instance.TriggerInfo).ToList());
            List<int[]> instanceFields = new List<int[]>();
            for (int i = 0; i < instances.Count; i++)
            {
                AbilityInstance instance = instances[i];
                instanceFields.Add(new[]
                {
                    instance.Id, (int)instance.State, instance.StartTrigger == null ? -1 : instance.StartTrigger.Id,
                    instance.CurrentNode == null ? -1 : instance.CurrentNode.Id, shares[i],
                });
            }
            lines.Add(OracleFormat.Queue(game.ActionManager.Actions.Count, instanceFields));
            MersenneTwisterRandom random = game.RandomGenerator;
            lines.Add(OracleFormat.Generator(random.InitialSeed, random.NumTimesCalledSinceCreation));
            return lines;
        }

        private static string CardLine(Card card)
        {
            CardPosition position = card.Position;
            CardPower power = card.Data.Power;
            int flags = (card.IsWaitingToDie ? OracleFormat.WaitingToDie : 0) | (card.IsInExecutionStack ? OracleFormat.InExecutionStack : 0) |
                        (card.IsBeingPlayed ? OracleFormat.BeingPlayed : 0) | (card.Data.CanBePlayed ? OracleFormat.CanBePlayed : 0);
            return OracleFormat.Card(card.Id, card.Template.Id, (int)position.PlayerId, (int)position.Location, position.Index, power.BasePower,
                                     power.PermanentPower, power.CurrentPower, power.CurrentArmor, (int)card.Data.Tokens.Tokens, card.Data.Timer.Value,
                                     (int)card.Visibility, flags, (int)card.PlayedBy, (int)card.FactionId);
        }

        private static string PlayerLine(Player player, bool copy)
        {
            BattleRestrictions restrictions = player.Restrictions;
            BattleSelectionOptions selection = restrictions.ForcedSelectionOptions;
            return OracleFormat.Player((int)player.Id, (int)player.Status, player.HasPassed, player.Crowns, player.HasMadeInitialMoveForCurrentTurn,
                                       player.NumTurnsWithoutPlaying, copy ? 0 : (int)restrictions.EnabledActions, (int)restrictions.ForceDisabledActions,
                                       restrictions.ForcePass, CardIds(restrictions.ForcePlayCards), Rows_(restrictions.ForcePlayLocations),
                                       OracleFormat.List(restrictions.ForcedTemplateTargets), CardIds(selection.ValidCards),
                                       Rows_(selection.ValidLocations), CardIds(restrictions.ForceMulliganCards));
        }

        private static string CardIds(List<Card> cards)
        {
            return OracleFormat.List(cards.Select(card => card == null ? -1 : (int)card.Id));
        }

        private static string Rows_(List<Location> rows)
        {
            return OracleFormat.Locations(rows.Select(row => new KeyValuePair<int, int>((int)row.PlayerId, (int)row.Loc)));
        }

        // The ability instance a request belongs to, if its type has one.
        private static readonly Dictionary<System.Type, PropertyInfo> s_InstanceProperty = new Dictionary<System.Type, PropertyInfo>();

        private static AbilityInstance RequestInstance(ARequestAction request)
        {
            System.Type type = request.GetType();
            PropertyInfo property;
            lock (s_InstanceProperty)
            {
                if (!s_InstanceProperty.TryGetValue(type, out property))
                {
                    property = s_InstanceProperty[type] = type.GetProperty("AbilityInstance", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            return property == null ? null : (AbilityInstance)property.GetValue(request, null);
        }
    }
}
