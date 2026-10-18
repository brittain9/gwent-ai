using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace GwentAI.Learn
{
    // The parsed weights of a net file, kept for the life of the process.
    // Parsing a policy net takes about 24 ms (widening, the template tables),
    // and one host process plays thousands of games, each of which asks for
    // its nets again. The cache holds one parsed "master" per file content
    // and never evaluates it; a caller gets a Fresh() instance that shares
    // the master's immutable weights and owns its scratch buffers and
    // counters, so the games stay independent and the play unchanged: a
    // cached net is the same net, byte for byte.
    //
    // Two keys: the file's full path with its length and write time (a hit
    // reads no file), and an MD5 of the bytes (a net from FromBytes, and two
    // paths with one content).
    internal sealed class NetCache<T> where T : class
    {
        private readonly Func<byte[], T> m_Parse;
        private readonly Dictionary<string, T> m_ByContent = new Dictionary<string, T>();
        private readonly Dictionary<string, T> m_ByFile = new Dictionary<string, T>();
        private readonly object m_Lock = new object();

        internal NetCache(Func<byte[], T> parse)
        {
            m_Parse = parse;
        }

        internal T FromBytes(byte[] bytes)
        {
            string key = Convert.ToBase64String(MD5.Create().ComputeHash(bytes));
            lock (m_Lock)
            {
                T master;
                if (!m_ByContent.TryGetValue(key, out master))
                {
                    master = m_Parse(bytes);
                    m_ByContent[key] = master;
                }
                return master;
            }
        }

        internal T FromFile(string path)
        {
            FileInfo info = new FileInfo(path);
            string key = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            lock (m_Lock)
            {
                T master;
                if (!m_ByFile.TryGetValue(key, out master))
                {
                    master = FromBytes(File.ReadAllBytes(path));
                    m_ByFile[key] = master;
                }
                return master;
            }
        }
    }
}
