using System;
using System.IO;
using GwentAI.Search;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GwentAI.Tests
{
    // ClassB.Is against class-b-states.json, a list of hand-built states that
    // exercise the class-B rule.
    public class ClassBTests
    {
        private static readonly JObject s_States = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "class-b-states.json")));

        private static bool Is(JToken state)
        {
            // The list counts rounds from 1, the engine's index from 0.
            return ClassB.Is(roundIndex: (int)state["round"] - 1, ownCrowns: (int)state["crownsMover"], opponentCrowns: (int)state["crownsOpponent"],
                             ownPassed: (bool)state["passedMover"], ownHand: (int)state["handMover"], opponentHand: (int)state["handOpponent"]);
        }

        public static TheoryData<int> CaseIndexes()
        {
            TheoryData<int> indexes = new TheoryData<int>();
            for (int index = 0; index < ((JArray)s_States["cases"]).Count; index++)
            {
                indexes.Add(index);
            }
            return indexes;
        }

        [Theory]
        [MemberData(nameof(CaseIndexes))]
        public void MatchesTheSharedList(int index)
        {
            JToken test = s_States["cases"][index];
            JObject state = (JObject)((JObject)s_States["base"]).DeepClone();
            state.Merge(test["state"]);
            Assert.True((bool)test["classB"] == Is(state), (string)test["name"]);
        }

        [Fact]
        public void TheListLeavesClassBOnEveryConditionAndKeepsItAtTheBase()
        {
            Assert.True(Is(s_States["base"]));
            foreach (string condition in new[] { "round", "crownsMover", "crownsOpponent", "passedMover", "hand" })
            {
                bool covered = false;
                foreach (JToken test in s_States["cases"])
                {
                    covered |= (string)test["flips"] == condition && !(bool)test["classB"];
                }
                Assert.True(covered, condition + " has no case that leaves class B");
            }
        }

        [Fact]
        public void RoundTwoIsIndexOne()
        {
            Assert.True(ClassB.Is(1, 1, 0, false, 3, 4));
            Assert.False(ClassB.Is(0, 1, 0, false, 3, 4));
            Assert.False(ClassB.Is(2, 1, 0, false, 3, 4));
        }
    }
}
