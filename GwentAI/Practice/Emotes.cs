using System;
using System.Collections.Generic;
using GwentAI.Search;

namespace GwentAI.Practice
{
    // The Practice opponent's taunts, like a person's: most
    // opponents never taunt, a few now and then, rarely one a lot. Each game
    // draws a persona from its seed, and each taunt answers something that
    // just happened in the game, with a small chance, so even a chatty
    // opponent is not predictable.
    //
    // This class decides only: it reads plain numbers (Moment) and never the
    // client, so its rules are tested without it (GwentAI.Tests/EmotesTests).
    // PracticeAI reads the live game, asks Decide at each of the AI's play
    // decisions and Ended at the game's end, and shows what it returns. The
    // draws come from a SplitMix64 stream of their own, salted off the game's
    // seed: never the game's or the search's random streams, so the AI's
    // decisions are the same with emotes on and off.
    public sealed class Emotes
    {
        // GWENT_AI_EMOTES=1 turns emotes on; any other value, or none, leaves
        // Practice as it was.
        public const string Variable = "GWENT_AI_EMOTES";

        public static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";

        // The persona shares: about half of opponents never taunt, about one
        // in ten is chatty, the rest taunt now and then.
        public const double SilentShare = 0.5;
        public const double ChattyShare = 0.1;
        // Taunts per game at most: a person who taunts more gets muted.
        public const int OccasionalCap = 2;
        public const int ChattyCap = 5;
        // At least this many of the AI's decisions without a taunt between two
        // taunts, so they never come in a row.
        public const int Cooldown = 3;
        // A chatty opponent takes each chance this many times as often (at most always).
        public const double ChattyBoost = 1.5;

        // A round lost by at most this many points was close: "Dammit!" (Curses).
        public const int NarrowLoss = 5;
        // A round lost after leading by at least this much at the AI's last
        // decision in it was a swing the human earned: "Well played".
        public const int SwingLead = 8;
        // The chance to answer a lost round that was close or a swing.
        public const double RoundLostChance = 0.4;
        // The search's value of the position (a win chance in [0, 1]) rose by
        // more than this since the AI's previous decision in the round: the
        // human's move helped the AI. "Bad move" (Mistake), chatty only, rarely.
        public const double MistakeRise = 0.15;
        public const double MistakeChance = 0.2;
        // A rise this large after the human passed: the pass handed the AI
        // the round. "Thanks".
        public const double ThanksRise = 0.25;
        public const double ThanksChance = 0.4;
        // Round 3, a lead of at least this many points and a card in hand:
        // "You're going down" (Threat), once a game at most.
        public const int ThreatLead = 20;
        public const double ThreatChance = 0.3;
        // About to play a gold: "Watch this" (Preempt1). Golds come up
        // several times a game, so the chance is small.
        public const double PreemptChance = 0.12;
        // The AI lost the game: "Well played". Nothing when it won: people
        // mute opponents who taunt after winning.
        public const double GameLostChance = 0.5;

        private const ulong Salt = 0x656D6F7465735F31UL;

        public enum Persona
        {
            Silent,
            Occasional,
            Chatty,
        }

        // What the AI's seat sees at one of its play decisions, before its
        // play is applied.
        public sealed class Moment
        {
            // The round being played, from 1.
            public int Round;
            // Rounds finished so far, and the AI's score minus the human's in
            // the latest of them.
            public int RoundsFinished;
            public int LastRoundMargin;
            // The AI's score minus the human's now, and the AI's cards in hand.
            public int ScoreLead;
            public int CardsInHand;
            public bool HumanPassed;
            // The search's value of the position for the AI, in [0, 1]; null
            // when this decision was not searched.
            public double? Value;
            // The AI's play is a gold card.
            public bool GoldPlay;
            // The human muted the opponent: say nothing and count nothing.
            public bool Muted;
        }

        // One taunt, as the game record keeps it.
        public sealed class Taunt
        {
            public int Decision;
            public int Round;
            public string Category;
            public string Trigger;
        }

        public readonly Persona Kind;
        public readonly List<Taunt> Sent = new List<Taunt>();
        private readonly Func<double> m_Uniform;
        private int m_Decisions;
        private int m_LastTaunt = -Cooldown - 1;
        private bool m_Threatened;
        private int m_RoundsSeen;
        private int m_LastLead;
        private double? m_LastValue;
        private int m_LastValueRound;

        public Emotes(int seed) : this(seed, null)
        {
        }

        // uniform replaces the stream's chance draws (tests force them); the
        // persona still comes from the seed.
        public Emotes(int seed, Func<double> uniform)
        {
            SplitMix64 random = new SplitMix64(((ulong)(uint)seed << 32) ^ Salt);
            Kind = Draw(random.NextDouble());
            m_Uniform = uniform ?? random.NextDouble;
        }

        public static Persona Draw(double uniform)
        {
            return uniform < SilentShare ? Persona.Silent : uniform < 1 - ChattyShare ? Persona.Occasional : Persona.Chatty;
        }

        public int Cap => Kind == Persona.Chatty ? ChattyCap : Kind == Persona.Occasional ? OccasionalCap : 0;

        // The taunt for this play decision, or null.
        public Taunt Decide(Moment now)
        {
            int decision = ++m_Decisions;
            int lead = m_LastLead;
            bool roundEnded = now.RoundsFinished > m_RoundsSeen;
            double? rise = now.Value.HasValue && m_LastValue.HasValue && m_LastValueRound == now.Round
                ? now.Value - m_LastValue : null;
            m_RoundsSeen = now.RoundsFinished;
            m_LastLead = now.ScoreLead;
            if (now.Value.HasValue)
            {
                m_LastValue = now.Value;
                m_LastValueRound = now.Round;
            }
            if (now.Muted || !Open(decision))
            {
                return null;
            }
            if (roundEnded && now.LastRoundMargin < 0)
            {
                if (-now.LastRoundMargin <= NarrowLoss && Roll(RoundLostChance))
                {
                    return Send(decision, now.Round, "Curses", "round lost narrowly");
                }
                if (lead >= SwingLead && Roll(RoundLostChance))
                {
                    return Send(decision, now.Round, "WellPlayed", "round lost to a swing");
                }
            }
            if (rise > ThanksRise && now.HumanPassed && Roll(ThanksChance))
            {
                return Send(decision, now.Round, "Thanks", "human passed into a lost round");
            }
            if (Kind == Persona.Chatty && rise > MistakeRise && Roll(MistakeChance))
            {
                return Send(decision, now.Round, "Mistake", "human's move raised the AI's value");
            }
            if (!m_Threatened && now.Round == 3 && now.ScoreLead >= ThreatLead && now.CardsInHand > 0 && Roll(ThreatChance))
            {
                m_Threatened = true;
                return Send(decision, now.Round, "Threat", "wide round-3 lead");
            }
            if (now.GoldPlay && Roll(PreemptChance))
            {
                return Send(decision, now.Round, "Preempt1", "gold play");
            }
            return null;
        }

        // The taunt at the game's end, or null: the game end counts as the
        // AI's next decision for the cap and the cooldown.
        public Taunt Ended(bool aiLost, int round, bool muted)
        {
            int decision = m_Decisions + 1;
            if (!aiLost || muted || !Open(decision) || !Roll(GameLostChance))
            {
                return null;
            }
            return Send(decision, round, "WellPlayed", "game lost");
        }

        private bool Open(int decision)
        {
            return Sent.Count < Cap && decision - m_LastTaunt > Cooldown;
        }

        private bool Roll(double chance)
        {
            return m_Uniform() < Math.Min(1, Kind == Persona.Chatty ? chance * ChattyBoost : chance);
        }

        private Taunt Send(int decision, int round, string category, string trigger)
        {
            m_LastTaunt = decision;
            Taunt taunt = new Taunt { Decision = decision, Round = round, Category = category, Trigger = trigger };
            Sent.Add(taunt);
            return taunt;
        }
    }
}
