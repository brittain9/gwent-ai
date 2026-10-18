using System;
using System.Linq;
using GwentAI.Practice;
using Xunit;

namespace GwentAI.Tests
{
    // The Practice opponent's taunts (Emotes): the persona from the
    // game's seed, the caps and the cooldown, and which event says what.
    public class EmotesTests
    {
        private static Emotes.Moment Quiet(int round = 1)
        {
            return new Emotes.Moment { Round = round, CardsInHand = 5 };
        }

        // The first seed (from 0) whose persona is the given one.
        private static int SeedOf(Emotes.Persona persona)
        {
            return Enumerable.Range(0, 1000).First(seed => new Emotes(seed).Kind == persona);
        }

        // Every chance taken: what fires is decided by the rules alone.
        private static Emotes Always(Emotes.Persona persona)
        {
            return new Emotes(SeedOf(persona), () => 0);
        }

        [Fact]
        public void PersonaIsTheSameForTheSameSeed()
        {
            foreach (int seed in new[] { 0, 1, 12345, -7, int.MaxValue })
            {
                Assert.Equal(new Emotes(seed).Kind, new Emotes(seed).Kind);
            }
        }

        [Fact]
        public void PersonaSharesMatchTheDesign()
        {
            // Practice seeds are System.Random.Next draws; consecutive seeds
            // are the harder case for a hash.
            int[] counts = new int[3];
            for (int seed = 0; seed < 1000; seed++)
            {
                counts[(int)new Emotes(seed).Kind]++;
            }
            // About half silent, about one in ten chatty, the rest occasional
            // (binomial, 1,000 games: about 4 standard errors).
            Assert.InRange(counts[(int)Emotes.Persona.Silent], 440, 560);
            Assert.InRange(counts[(int)Emotes.Persona.Chatty], 60, 140);
            Assert.InRange(counts[(int)Emotes.Persona.Occasional], 340, 460);
        }

        [Theory]
        [InlineData(Emotes.Persona.Silent, 0)]
        [InlineData(Emotes.Persona.Occasional, Emotes.OccasionalCap)]
        [InlineData(Emotes.Persona.Chatty, Emotes.ChattyCap)]
        public void CapAndCooldownHoldWhenEveryChanceIsTaken(Emotes.Persona persona, int cap)
        {
            Emotes emotes = Always(persona);
            // A gold every decision: a taunt whenever the rules allow one.
            for (int decision = 0; decision < 60; decision++)
            {
                Emotes.Moment now = Quiet();
                now.GoldPlay = true;
                emotes.Decide(now);
            }
            Assert.Equal(cap, emotes.Sent.Count);
            for (int i = 1; i < emotes.Sent.Count; i++)
            {
                Assert.True(emotes.Sent[i].Decision - emotes.Sent[i - 1].Decision > Emotes.Cooldown);
            }
            Assert.Null(emotes.Ended(aiLost: true, round: 3, muted: false));
        }

        [Fact]
        public void NarrowRoundLossCurses()
        {
            Emotes emotes = Always(Emotes.Persona.Occasional);
            emotes.Decide(Quiet());
            Emotes.Moment next = Quiet(2);
            next.RoundsFinished = 1;
            next.LastRoundMargin = -3;
            Assert.Equal("Curses", emotes.Decide(next).Category);
        }

        [Fact]
        public void LostLeadIsWellPlayedAndAWideLossIsNothing()
        {
            Emotes swing = Always(Emotes.Persona.Occasional);
            Emotes.Moment ahead = Quiet();
            ahead.ScoreLead = 12;
            swing.Decide(ahead);
            Emotes.Moment next = Quiet(2);
            next.RoundsFinished = 1;
            next.LastRoundMargin = -9;
            Assert.Equal("WellPlayed", swing.Decide(next).Category);

            Emotes behind = Always(Emotes.Persona.Occasional);
            behind.Decide(Quiet());
            Assert.Null(behind.Decide(next));
        }

        [Fact]
        public void RoundWonOrSeenBeforeSaysNothing()
        {
            Emotes emotes = Always(Emotes.Persona.Occasional);
            Emotes.Moment won = Quiet(2);
            won.RoundsFinished = 1;
            won.LastRoundMargin = 4;
            Assert.Null(emotes.Decide(won));
        }

        [Fact]
        public void ValueRiseIsMistakeForChattyOnly()
        {
            foreach (Emotes.Persona persona in new[] { Emotes.Persona.Occasional, Emotes.Persona.Chatty })
            {
                Emotes emotes = Always(persona);
                Emotes.Moment before = Quiet();
                before.Value = 0.4;
                emotes.Decide(before);
                Emotes.Moment after = Quiet();
                after.Value = 0.6;
                Emotes.Taunt taunt = emotes.Decide(after);
                Assert.Equal(persona == Emotes.Persona.Chatty ? "Mistake" : null, taunt?.Category);
            }
        }

        [Fact]
        public void ValueRiseAcrossRoundsIsNotAMistake()
        {
            Emotes emotes = Always(Emotes.Persona.Chatty);
            Emotes.Moment before = Quiet(1);
            before.Value = 0.2;
            emotes.Decide(before);
            Emotes.Moment after = Quiet(2);
            after.Value = 0.9;
            Assert.Null(emotes.Decide(after));
        }

        [Fact]
        public void LargeRiseAfterTheHumanPassedIsThanks()
        {
            Emotes emotes = Always(Emotes.Persona.Occasional);
            Emotes.Moment before = Quiet();
            before.Value = 0.3;
            emotes.Decide(before);
            Emotes.Moment after = Quiet();
            after.Value = 0.7;
            after.HumanPassed = true;
            Assert.Equal("Thanks", emotes.Decide(after).Category);
        }

        [Fact]
        public void ThreatNeedsRoundThreeAWideLeadAndCardsAndComesOnce()
        {
            Emotes emotes = Always(Emotes.Persona.Chatty);
            Emotes.Moment round2 = Quiet(2);
            round2.ScoreLead = 30;
            Assert.Null(emotes.Decide(round2));
            Emotes.Moment empty = Quiet(3);
            empty.ScoreLead = 30;
            empty.CardsInHand = 0;
            Assert.Null(emotes.Decide(empty));
            Emotes.Moment ahead = Quiet(3);
            ahead.ScoreLead = Emotes.ThreatLead;
            Assert.Equal("Threat", emotes.Decide(ahead).Category);
            for (int i = 0; i < 10; i++)
            {
                Assert.Null(emotes.Decide(ahead));
            }
        }

        [Fact]
        public void GoldPlayIsPreempt()
        {
            Emotes emotes = Always(Emotes.Persona.Occasional);
            Emotes.Moment gold = Quiet();
            gold.GoldPlay = true;
            Assert.Equal("Preempt1", emotes.Decide(gold).Category);
        }

        [Fact]
        public void GameEndIsWellPlayedOnlyWhenTheAILost()
        {
            Assert.Null(Always(Emotes.Persona.Occasional).Ended(aiLost: false, round: 3, muted: false));
            Assert.Equal("WellPlayed", Always(Emotes.Persona.Occasional).Ended(aiLost: true, round: 3, muted: false).Category);
            Assert.Null(Always(Emotes.Persona.Silent).Ended(aiLost: true, round: 3, muted: false));
        }

        [Fact]
        public void MutedSaysAndCountsNothing()
        {
            Emotes emotes = Always(Emotes.Persona.Chatty);
            Emotes.Moment gold = Quiet();
            gold.GoldPlay = true;
            gold.Muted = true;
            Assert.Null(emotes.Decide(gold));
            Assert.Empty(emotes.Sent);
            Assert.Null(emotes.Ended(aiLost: true, round: 3, muted: true));
        }

        [Fact]
        public void NoChanceTakenSaysNothing()
        {
            Emotes emotes = new Emotes(SeedOf(Emotes.Persona.Chatty), () => 0.999);
            for (int decision = 0; decision < 40; decision++)
            {
                Emotes.Moment gold = Quiet(3);
                gold.GoldPlay = true;
                gold.ScoreLead = 40;
                Assert.Null(emotes.Decide(gold));
            }
        }

        [Fact]
        public void ChattyTauntsMoreOftenThanOccasionalOverManyGames()
        {
            // A plain game, drawn from each seed's own stream: ten decisions,
            // two golds, a round 3 lead; the persona alone sets the rate.
            double[] rate = new double[3];
            int[] games = new int[3];
            for (int seed = 0; seed < 1000; seed++)
            {
                Emotes emotes = new Emotes(seed);
                for (int decision = 0; decision < 10; decision++)
                {
                    Emotes.Moment now = Quiet(decision < 7 ? 1 : 3);
                    now.GoldPlay = decision == 2 || decision == 5;
                    now.ScoreLead = decision < 7 ? 0 : 25;
                    emotes.Decide(now);
                }
                rate[(int)emotes.Kind] += emotes.Sent.Count;
                games[(int)emotes.Kind]++;
            }
            Assert.Equal(0, rate[(int)Emotes.Persona.Silent]);
            Assert.True(rate[(int)Emotes.Persona.Chatty] / games[(int)Emotes.Persona.Chatty]
                        > rate[(int)Emotes.Persona.Occasional] / games[(int)Emotes.Persona.Occasional]);
        }
    }
}
