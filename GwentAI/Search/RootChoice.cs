using System;
using System.Collections.Generic;
using System.Linq;
using GwentGameplay;

namespace GwentAI.Search
{
    // Changing the step a finished search chose (the economy tie-break, the
    // second look).
    public static class RootChoice
    {
        // Moves the root step at index to the front of the result's root and
        // makes it the chosen step. A card that only picks the card (a
        // grouped play) takes its most visited position from the search's own
        // children of that card. legal: the steps legal at the root.
        public static void Promote(SearchResult result, int index, ARequestAction request, Player seat, SearchSettings settings, List<GameAction> legal)
        {
            RootStat chosen = result.Root[index];
            result.Root.RemoveAt(index);
            result.Root.Insert(0, chosen);
            result.Action = Legal(legal, chosen.Key);
            result.NextRoot = null;
            result.Next = null;
            if (result.Action.Move == null)
            {
                result.NextRoot = chosen.Children;
                RootStat next = chosen.Children?.FirstOrDefault();
                result.Next = next == null ? null
                    : Actions.Legal(request, seat, new List<string> { chosen.Key }, settings.GroupPlays).Find(action => action.Key == next.Key);
            }
        }

        // Each root child is a live step (Ismcts.Finish, WorkerSearch.Merge).
        public static GameAction Legal(List<GameAction> legal, string key)
        {
            GameAction action = legal.Find(candidate => candidate.Key == key);
            if (action == null)
            {
                throw new InvalidOperationException($"Root step {key} is not legal in the live game");
            }
            return action;
        }
    }
}
