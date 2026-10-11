using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GwentAI.Search;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GwentAI.Tests
{
    // CardRoles is process-wide; serialize the tests that initialize it and
    // share their immutable file pair. Loader tests use private directories
    // and LoadDirectory, which never changes the process-wide table.
    [CollectionDefinition("Card data")]
    public sealed class CardDataCollection : ICollectionFixture<CardDataFixture>
    {
    }

    // Files, rather than embedded client data, exercise the runtime loader.
    // Synthetic client fingerprints keep these unit tests engine-free.
    public sealed class CardDataFixture : IDisposable
    {
        public readonly string Client = Path.Combine(Path.GetTempPath(), "gwent-card-tests-" + Guid.NewGuid().ToString("N"));
        public string Directory => Path.Combine(Client, "UserData/GwentAI/cards");
        public string Engine => Path.Combine(Client, "Gwent_Data/Managed/Assembly-CSharp.dll");
        public string Definitions => Path.Combine(Client, "Gwent_Data/StreamingAssets/data_definitions");
        public bool HasNumbers { get; private set; }

        public CardDataFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Engine));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Definitions));
            File.WriteAllText(Engine, "synthetic engine");
            File.WriteAllText(Definitions, "synthetic card definitions");
            string source = Path.Combine(AppContext.BaseDirectory, "card-fixtures");
            string roles = File.ReadAllText(Path.Combine(source, "roles.json"));
            string numbers = Path.Combine(source, "answer-params.json");
            HasNumbers = File.Exists(numbers);
            Write(roles, HasNumbers ? File.ReadAllText(numbers) : new JObject { ["roles_sha256"] = Hash(roles) }.ToString());
        }

        public void Write(string roles, string numbers)
        {
            File.WriteAllText(Path.Combine(Directory, "roles.json"), roles, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Directory, "answer-params.json"), numbers, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Directory, "manifest.json"), new JObject
            {
                ["engine_sha256"] = Hash(File.ReadAllText(Engine)),
                ["definitions_sha256"] = Hash(File.ReadAllText(Definitions)),
            }.ToString());
        }

        public static string Hash(string text)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            }
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Client, recursive: true);
        }
    }
}
