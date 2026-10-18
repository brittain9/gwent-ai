using System;
using System.Collections.Generic;
using System.Linq;
using GwentAI.Practice;
using Xunit;

namespace GwentAI.Tests
{
    // The AI's arena draft (ArenaDraft): the local server's schedule
    // (server/deploy/arena.py draft_offers) and the deck it gives.
    public class ArenaDraftTests
    {
        private static readonly List<int> Golds = Enumerable.Range(100, 60).ToList();
        private static readonly List<int> Silvers = Enumerable.Range(200, 60).ToList();
        private static readonly List<int> Bronzes = Enumerable.Range(300, 120).ToList();
        private static readonly List<int> Leaders = Enumerable.Range(1, 15).ToList();

        [Fact]
        public void EveryDraftIsNineGoldEightSilverNineBronzeSingletonsFromTheirPools()
        {
            Random random = new Random(7);
            for (int draft = 0; draft < 300; draft++)
            {
                ArenaDraft.Result result = ArenaDraft.Draft(Golds, Silvers, Bronzes, Leaders, random);
                Assert.Equal(26, result.Cards.Count);
                Assert.Equal(26, result.Cards.Distinct().Count());
                Assert.Equal(9, result.Tiers.Count(tier => tier == ArenaDraft.Tier.Gold));
                Assert.Equal(8, result.Tiers.Count(tier => tier == ArenaDraft.Tier.Silver));
                Assert.Equal(9, result.Tiers.Count(tier => tier == ArenaDraft.Tier.Bronze));
                Assert.Contains(result.Leader, Leaders);
                for (int i = 0; i < result.Cards.Count; i++)
                {
                    List<int> pool = result.Tiers[i] == ArenaDraft.Tier.Gold ? Golds : result.Tiers[i] == ArenaDraft.Tier.Silver ? Silvers : Bronzes;
                    Assert.Contains(result.Cards[i], pool);
                }
            }
        }

        // The schedule is shuffled: the tiers do not come in a fixed order.
        [Fact]
        public void TheTierScheduleIsShuffled()
        {
            Random random = new Random(11);
            HashSet<string> orders = new HashSet<string>();
            for (int draft = 0; draft < 20; draft++)
            {
                orders.Add(string.Join(",", ArenaDraft.Draft(Golds, Silvers, Bronzes, Leaders, random).Tiers));
            }
            Assert.True(orders.Count > 15);
        }

        // A pool too small for every full offer still drafts while it lasts
        // (the last offers shrink); a tier that runs out is an error, not a
        // short deck. 9 gold steps of 4 offered: 33 golds last, 32 do not.
        [Fact]
        public void APoolThatRunsOutThrows()
        {
            ArenaDraft.Result result = ArenaDraft.Draft(Enumerable.Range(1000, 33).ToList(), Enumerable.Range(2000, 29).ToList(), Enumerable.Range(3000, 33).ToList(), new[] { 99 }, new Random(3));
            Assert.Equal(26, result.Cards.Distinct().Count());
            Assert.Equal(99, result.Leader);
            Assert.Throws<ArgumentException>(() => ArenaDraft.Draft(Enumerable.Range(1000, 32).ToList(), Enumerable.Range(2000, 29).ToList(), Enumerable.Range(3000, 33).ToList(), new[] { 99 }, new Random(3)));
        }
    }
}
