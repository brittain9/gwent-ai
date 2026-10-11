using System.IO;
using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    public class CardDataLoadingTests
    {
        private const string Roles = "{\"cards\":{\"1\":{\"id\":1,\"roles\":[\"removal\"],\"answer_kinds\":[{\"kind\":\"damage_fixed\"}]}}}";

        [Fact]
        public void ALocalPairKeepsItsNumericAbilityParameters()
        {
            using (CardDataFixture files = new CardDataFixture())
            {
                files.Write(Roles, "{\"roles_sha256\":\"" + CardDataFixture.Hash(Roles) + "\",\"cards\":{\"1\":{\"answer_kinds\":[{\"amount\":4}]}}}");
                Assert.Equal(4, CardRoles.LoadDirectory(files.Directory, files.Client).Answers[1][0].Amount);
            }
        }

        [Theory]
        [InlineData("roles.json")]
        [InlineData("answer-params.json")]
        [InlineData("manifest.json")]
        public void MissingDataFailsBeforeTheAiCanPlay(string name)
        {
            using (CardDataFixture files = new CardDataFixture())
            {
                File.Delete(Path.Combine(files.Directory, name));
                FileNotFoundException error = Assert.Throws<FileNotFoundException>(() => CardRoles.LoadDirectory(files.Directory, files.Client));
                Assert.Contains("run setup", error.Message);
            }
        }

        [Fact]
        public void ParametersForAnotherRoleTableFail()
        {
            using (CardDataFixture files = new CardDataFixture())
            {
                files.Write(Roles, "{\"roles_sha256\":\"another table\"}");
                Assert.Throws<InvalidDataException>(() => CardRoles.LoadDirectory(files.Directory, files.Client));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ChangedEngineOrCardDefinitionsRequireRegeneration(bool engine)
        {
            using (CardDataFixture files = new CardDataFixture())
            {
                File.AppendAllText(engine ? files.Engine : files.Definitions, "changed");
                InvalidDataException error = Assert.Throws<InvalidDataException>(() => CardRoles.LoadDirectory(files.Directory, files.Client));
                Assert.Contains("rerun setup", error.Message);
            }
        }
    }
}
