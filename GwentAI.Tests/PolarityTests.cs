using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // CardRoles.Polarity (Rollout.Aim) from the roles.json payoffs.
    public class PolarityTests
    {
        private const int ViperWitcher = 200124, TuirseachHunter = 152304, Swallow = 113310, Mandrake = 200223, VicovaroNovice = 122403;

        [Fact]
        public void DamageThatMayHitEitherSideAimsAtTheOpponent()
        {
            Assert.Equal(-1, CardRoles.Polarity(ViperWitcher));
            Assert.Equal(-1, CardRoles.Polarity(TuirseachHunter));
        }

        [Fact]
        public void ABoostThatMayHitEitherSideAimsAtItsOwnSide()
        {
            Assert.Equal(1, CardRoles.Polarity(Swallow));
        }

        [Fact]
        public void AModalCardOrOneWithoutSuchATargetFavoursNoSide()
        {
            Assert.Equal(0, CardRoles.Polarity(Mandrake));
            Assert.Equal(0, CardRoles.Polarity(VicovaroNovice));
        }
    }
}
