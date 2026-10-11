using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GwentCore;
using GwentGameplay;
using ICSharpCode.SharpZipLib.Zip;
using RedTools;

namespace GwentHost
{
    // Serves the engine the assets the client loads, read from the client
    // install itself: card and AI definitions from the data_definitions
    // archive (as DefinitionsPackage does) and GwentSettings, whose AI
    // scoring weights the stock search reads, from the "Settings" TextAsset
    // in resources.assets (as Resources.Load does).
    public class ClientAssets : AssetManager
    {
        private readonly Dictionary<string, string> m_Definitions;
        private readonly string m_Settings;

        public ClientAssets(string clientDir)
        {
            string data = Path.Combine(clientDir, "Gwent_Data");
            m_Definitions = ReadArchive(Path.Combine(data, "StreamingAssets/data_definitions"));
            m_Settings = ReadTextAsset(Path.Combine(data, "resources.assets"), "Settings");
            // Info is the data, game and services versions, one per line.
            string[] info = m_Definitions["Info"].Replace("\r", string.Empty).Split('\n');
            DataVersion = info[0];
            ServicesDataVersion = info[2];
        }

        public override Env Environment => Env.prod;

        public override string GetAsset(EAssetName name)
        {
            if (name == EAssetName.Settings)
            {
                return m_Settings;
            }
            string entry = EntryName(name);
            return entry == null ? string.Empty : m_Definitions[entry];
        }

        // UnityAssetManager's names for the archive entries.
        private static string EntryName(EAssetName name)
        {
            switch (name)
            {
                case EAssetName.CardTemplates: return "Templates.xml";
                case EAssetName.CardAbilities: return "Abilities.xml";
                case EAssetName.Personalities: return "Personalities.xml";
                case EAssetName.Summations: return "Summations.xml";
                case EAssetName.Categories: return "Categories.xml";
                case EAssetName.CardArtData: return "Artists.xml";
                case EAssetName.CardAudio: return "CardAudio.xml";
                default: return null;
            }
        }

        private static Dictionary<string, string> ReadArchive(string path)
        {
            Dictionary<string, string> entries = new Dictionary<string, string>();
            using (ZipFile zip = new ZipFile(path))
            {
                foreach (ZipEntry entry in zip)
                {
                    if (!entry.IsFile)
                    {
                        continue;
                    }
                    using (StreamReader reader = new StreamReader(zip.GetInputStream(entry), Encoding.UTF8))
                    {
                        entries.Add(entry.Name, reader.ReadToEnd());
                    }
                }
            }
            return entries;
        }

        // A serialized TextAsset is its name (int32 length, bytes, padded to
        // 4) followed by its text (int32 length, bytes). The client's XML
        // assets are found by that framing; exactly one match is required.
        private static string ReadTextAsset(string path, string name)
        {
            byte[] bytes = File.ReadAllBytes(path);
            byte[] key = Encoding.ASCII.GetBytes(name);
            int nameEnd = 4 + (key.Length + 3) / 4 * 4;
            string text = null;
            for (int i = 0; i + nameEnd + 4 <= bytes.Length; i++)
            {
                if (BitConverter.ToInt32(bytes, i) != key.Length || !Matches(bytes, i + 4, key))
                {
                    continue;
                }
                int length = BitConverter.ToInt32(bytes, i + nameEnd);
                int start = i + nameEnd + 4;
                if (length <= 0 || start + length > bytes.Length || !Matches(bytes, start, s_XmlDeclaration))
                {
                    continue;
                }
                if (text != null)
                {
                    throw new InvalidDataException($"{path}: more than one TextAsset named {name}");
                }
                text = Encoding.UTF8.GetString(bytes, start, length);
            }
            return text ?? throw new InvalidDataException($"{path}: no TextAsset named {name}");
        }

        private static readonly byte[] s_XmlDeclaration = Encoding.ASCII.GetBytes("<?xml");

        private static bool Matches(byte[] bytes, int offset, byte[] pattern)
        {
            if (offset + pattern.Length > bytes.Length)
            {
                return false;
            }
            for (int i = 0; i < pattern.Length; i++)
            {
                if (bytes[offset + i] != pattern[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
