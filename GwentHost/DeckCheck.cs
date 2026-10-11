using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GwentAI.Practice;
using GwentCore;
using GwentGameplay;
using Newtonsoft.Json;

namespace GwentHost
{
    // Checks deck files with the engine's deck validator and the client's
    // default rules (25-40 cards, at most 4 golds and 6 silvers, copy limits,
    // one faction plus Neutral): the last word on whether a deck is legal.
    public static class DeckCheck
    {
        // Kept out of Main so no engine type is touched before the resolver
        // is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run(string client, List<string> paths)
        {
            RedLogger.ConsoleLogger logger = new RedLogger.ConsoleLogger();
            GameSharedData shared = GameSharedData.Create(new ClientAssets(client), logger);
            DeckValidator validator = new DeckValidator(new DefaultDeckValidatorRuleSet());
            int invalid = 0;
            foreach (string path in paths)
            {
                DeckSpec deck = JsonConvert.DeserializeObject<DeckSpec>(System.IO.File.ReadAllText(path));
                List<CardTemplate> templates = new List<CardTemplate> { shared.GetSharedRuntimeTemplate(deck.Leader.Id).Template };
                templates.AddRange(deck.Cards.SelectMany(card => Enumerable.Repeat(shared.GetSharedRuntimeTemplate(card.Id).Template, card.Count)));
                DeckValidatorResult result = validator.Validate(templates);
                if (!result.IsDeckValid)
                {
                    invalid++;
                }
                Console.WriteLine($"{(result.IsDeckValid ? "valid" : "INVALID")}\t{result.TotalCardCount} cards, {result.GoldCardCount} gold, " +
                                  $"{result.SilverCardCount} silver, {result.BronzeCardCount} bronze\t{path}");
            }
            return invalid == 0 ? 0 : 1;
        }
    }
}
