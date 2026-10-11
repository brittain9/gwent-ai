using System;
using System.Collections.Generic;
using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // The work budget: the counts, their weights and the checkpoints
    // the workers stop at. The search itself needs the engine.
    public class WorkBudgetTests
    {
        [Fact]
        public void AWorkBudgetNeedsOneTreePerWorker()
        {
            WorkCheckpoints.RequireOneTreePerWorker(6, 6);
            foreach (int trees in new[] { 1, 3, 12 })
            {
                Assert.Contains("as many trees as workers", Assert.Throws<ArgumentException>(() => WorkCheckpoints.RequireOneTreePerWorker(trees, 6)).Message);
            }
            Assert.Contains("needs search workers", Assert.Throws<ArgumentException>(() => WorkCheckpoints.RequireOneTreePerWorker(1, 0)).Message);
        }

        [Fact]
        public void UnitsWeighEachOperationOnce()
        {
            long units = WorkWeights.Units(simulations: 3, actions: 5, valuePasses: 7, rolloutPasses: 11, priorPasses: 13);
            Assert.Equal(3 * WorkWeights.Simulation + 5 * WorkWeights.Action + 7 * WorkWeights.ValuePass + 11 * WorkWeights.RolloutPass + 13 * WorkWeights.PriorPass, units);
            Assert.Equal(0, WorkWeights.Units(0, 0, 0, 0, 0));
        }

        [Fact]
        public void TalliesAddFieldByFieldAndSkipMissingOnes()
        {
            WorkTally a = new WorkTally { Simulations = 1, Actions = 2, ValuePasses = 3, RolloutPasses = 4, PriorPasses = 5, ValueMs = 0.5, RolloutMs = 1.5, PriorMs = 2.5, Units = 100 };
            WorkTally b = new WorkTally { Simulations = 10, Actions = 20, ValuePasses = 30, RolloutPasses = 40, PriorPasses = 50, ValueMs = 1, RolloutMs = 1, PriorMs = 1, Units = 1000 };
            WorkTally total = WorkTally.Sum(new[] { a, null, b });
            Assert.Equal(11, total.Simulations);
            Assert.Equal(22, total.Actions);
            Assert.Equal(33, total.ValuePasses);
            Assert.Equal(44, total.RolloutPasses);
            Assert.Equal(55, total.PriorPasses);
            Assert.Equal(1.5, total.ValueMs);
            Assert.Equal(2.5, total.RolloutMs);
            Assert.Equal(3.5, total.PriorMs);
            Assert.Equal(1100, total.Units);
        }

        [Fact]
        public void ABudgetHasSixteenCheckpointsAndNeverZeroSpacing()
        {
            Assert.Equal(1250000000, WorkCheckpoints.Spacing(20000 * WorkWeights.UnitsPerMs));
            Assert.Equal(1, WorkCheckpoints.Spacing(0));
            Assert.Equal(1, WorkCheckpoints.Spacing(5));
        }

        [Fact]
        public void TheShareUsedIsTheLargerOfWorkAndIterationsOverEveryWorker()
        {
            // 6 workers with a budget of 100 units and 1000 iterations each.
            Assert.Equal(0.5, WorkCheckpoints.Used(300, 100, 100, 1000, 6));
            Assert.Equal(0.5, WorkCheckpoints.Used(30, 100, 3000, 1000, 6));
            Assert.Equal(0, WorkCheckpoints.Used(0, 100, 0, 1000, 6));
        }

        [Fact]
        public void AWorkerReportsAtEachMultipleOfTheSpacingByItsOwnWorkAlone()
        {
            // Two workers with different costs per iteration pass the same
            // multiples of the spacing at different iterations, and each at the
            // same iterations on every run: nothing but its own work decides.
            Assert.Equal(new List<int> { 4, 8, 12 }, ReportingIterations(spacing: 100, perIteration: 25, iterations: 12));
            Assert.Equal(new List<int> { 3, 6, 9, 12 }, ReportingIterations(spacing: 100, perIteration: 34, iterations: 12));
            Assert.Equal(ReportingIterations(100, 34, 12), ReportingIterations(100, 34, 12));
        }

        [Fact]
        public void AnIterationThatPassesTwoMultiplesReportsTwiceInARow()
        {
            WorkCheckpoints.Clock clock = new WorkCheckpoints.Clock(100);
            Assert.False(clock.Reached(99));
            Assert.True(clock.Reached(250));
            Assert.True(clock.Reached(251));
            Assert.False(clock.Reached(252));
            Assert.True(clock.Reached(300));
        }

        private static List<int> ReportingIterations(long spacing, long perIteration, int iterations)
        {
            WorkCheckpoints.Clock clock = new WorkCheckpoints.Clock(spacing);
            List<int> reports = new List<int>();
            for (int i = 1; i <= iterations; i++)
            {
                if (clock.Reached(i * perIteration))
                {
                    reports.Add(i);
                }
            }
            return reports;
        }
    }
}
