using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace GwentHost
{
    // Loads the client's engine assemblies into CoreCLR.
    //
    // The obfuscator nested a few helper types (7 per obfuscated assembly)
    // inside <Module>, the global type. Mono accepts that; CoreCLR never
    // registers <Module> as a class, so loading the assembly fails with
    // "BadImageFormatException: Enclosing type(s) not found". The loader
    // makes those types top-level in a copy of the image in memory: their
    // NestedClass rows become empty (0, 0) rows at the table's start, which
    // keeps the table sorted and the same size, and their visibility becomes
    // the top-level equivalent. No method body, token or other table
    // changes, and nothing is written to disk.
    //
    // The BCL (mscorlib, System, System.*) always comes from .NET: its
    // facades forward the client's references to the framework's types.
    public static class EngineLoader
    {
        private static string[] s_Directories;
        private static readonly Dictionary<string, Assembly> s_Loaded = new Dictionary<string, Assembly>();
        private static readonly Dictionary<Assembly, byte[]> s_Images = new Dictionary<Assembly, byte[]>();

        public static void Install(params string[] directories)
        {
            s_Directories = directories;
            AssemblyLoadContext.Default.Resolving += Resolve;
        }

        private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            if (name.Name == "mscorlib" || name.Name == "System" || name.Name.StartsWith("System."))
            {
                return null;
            }
            lock (s_Loaded)
            {
                if (s_Loaded.TryGetValue(name.Name, out Assembly loaded))
                {
                    return loaded;
                }
                foreach (string directory in s_Directories)
                {
                    string path = Path.Combine(directory, name.Name + ".dll");
                    if (File.Exists(path))
                    {
                        byte[] image = Unnest(File.ReadAllBytes(path));
                        loaded = context.LoadFromStream(new MemoryStream(image));
                        s_Loaded.Add(name.Name, loaded);
                        s_Images.Add(loaded, image);
                        return loaded;
                    }
                }
                return null;
            }
        }

        // The image a loaded engine assembly was loaded from.
        public static byte[] Image(Assembly assembly)
        {
            lock (s_Loaded)
            {
                return s_Images[assembly];
            }
        }

        private const int GlobalTypeRow = 1;

        // The image with every type nested in <Module> made top-level.
        public static byte[] Unnest(byte[] image)
        {
            using PEReader pe = new PEReader(new MemoryStream(image));
            MetadataReader metadata = pe.GetMetadataReader();
            int start = pe.PEHeaders.MetadataStartOffset;
            int nested = start + metadata.GetTableMetadataOffset(TableIndex.NestedClass);
            int nestedRowSize = metadata.GetTableRowSize(TableIndex.NestedClass);
            int nestedRows = metadata.GetTableRowCount(TableIndex.NestedClass);
            int typeDefs = start + metadata.GetTableMetadataOffset(TableIndex.TypeDef);
            int typeDefRowSize = metadata.GetTableRowSize(TableIndex.TypeDef);
            // Both columns index the TypeDef table, so they are equally wide.
            int column = nestedRowSize / 2;
            byte[] patched = (byte[])image.Clone();
            List<byte[]> kept = new List<byte[]>();
            int moved = 0;
            for (int row = 0; row < nestedRows; row++)
            {
                int at = nested + row * nestedRowSize;
                if (ReadIndex(image, at + column, column) != GlobalTypeRow)
                {
                    kept.Add(image.AsSpan(at, nestedRowSize).ToArray());
                    continue;
                }
                moved++;
                // TypeDef.Flags is the row's first column (4 bytes).
                int flags = typeDefs + (ReadIndex(image, at, column) - 1) * typeDefRowSize;
                uint attributes = BitConverter.ToUInt32(image, flags);
                TypeAttributes visibility = (TypeAttributes)attributes & TypeAttributes.VisibilityMask;
                TypeAttributes topLevel = visibility == TypeAttributes.NestedPublic ? TypeAttributes.Public : TypeAttributes.NotPublic;
                BitConverter.GetBytes((attributes & ~(uint)TypeAttributes.VisibilityMask) | (uint)topLevel).CopyTo(patched, flags);
            }
            if (moved == 0)
            {
                return image;
            }
            Array.Clear(patched, nested, moved * nestedRowSize);
            for (int row = 0; row < kept.Count; row++)
            {
                kept[row].CopyTo(patched, nested + (moved + row) * nestedRowSize);
            }
            return patched;
        }

        private static int ReadIndex(byte[] image, int at, int size)
        {
            return size == 2 ? BitConverter.ToUInt16(image, at) : BitConverter.ToInt32(image, at);
        }
    }
}
