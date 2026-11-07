using BrawlLib.Internal;
using BrawlLib.SSBB.ResourceNodes;
using BrawlLib.Wii.Animations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrawlCrate.Cli
{
    /// <summary>
    /// Headless front end for BrawlLib's existing exporters: lists a file's node tree, or exports every
    /// BRRES entry in it (including BRRES files nested in U8/ARC/SZS containers) to a folder, like the
    /// GUI's "Export All", with a chosen format per resource type.
    /// </summary>
    internal static class Program
    {
        private const string Usage =
            "Usage:\n" +
            "  BrawlCrate.Cli list <input>\n" +
            "  BrawlCrate.Cli export <input> <out-dir> [--textures <ext>] [--models <ext>] [--anims <ext>]\n" +
            "                                          [--anim-model <name>]\n" +
            "\n" +
            "export writes each BRRES entry as <out-dir>/[<brres>/]<entry>.<ext>. A BRRES nested in a\n" +
            "container gets its own subfolder. Defaults keep the raw formats (.tex0, .mdl0, .chr0, ...).\n" +
            "  --textures   png, tga, tif, bmp, jpg, tex0 or none\n" +
            "  --models     dae, mdl0 or none\n" +
            "  --anims      anim (Maya ASCII), dae, chr0 or none\n" +
            "  --anim-model MDL0 whose bones a Maya .anim is written for. Default: the first model in the\n" +
            "               same BRRES.\n" +
            "\n" +
            "On macOS/Linux ARM64, run under the Mono interpreter (mono --interpreter): the ARM64 JIT\n" +
            "miscopies structs with fixed-size buffers, which corrupts BrawlLib's matrices.";

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 2 && args[0] == "list")
                {
                    return List(args[1]);
                }

                if (args.Length >= 3 && args[0] == "export")
                {
                    return Export(args[1], args[2], ParseOptions(args.Skip(3).ToArray()));
                }

                Console.Error.WriteLine(Usage);
                return 2;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"error: {e}");
                return 1;
            }
        }

        // "--textures none" etc. skips that resource type.
        private const string SkipExtension = ".none";

        private class ExportOptions
        {
            public string TextureExtension = ".tex0";
            public string ModelExtension = ".mdl0";
            public string AnimationExtension = ".chr0";
            public string AnimationModel;
        }

        private static ExportOptions ParseOptions(string[] args)
        {
            ExportOptions options = new ExportOptions();
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"missing value for {args[i]}");
                }

                string value = args[i + 1];
                switch (args[i])
                {
                    case "--textures":
                        options.TextureExtension = NormalizeExtension(value);
                        break;
                    case "--models":
                        options.ModelExtension = NormalizeExtension(value);
                        break;
                    case "--anims":
                        options.AnimationExtension = NormalizeExtension(value);
                        break;
                    case "--anim-model":
                        options.AnimationModel = value;
                        break;
                    default:
                        throw new ArgumentException($"unknown option {args[i]}");
                }
            }

            return options;
        }

        private static string NormalizeExtension(string extension)
        {
            return "." + extension.TrimStart('.').ToLowerInvariant();
        }

        private static ResourceNode Open(string path)
        {
            CheckRuntime();
            ResourceNode root = NodeFactory.FromFile(null, path);
            if (root == null)
            {
                throw new InvalidDataException($"{path} is not a format BrawlLib can open");
            }

            return root;
        }

        /// <summary>
        /// Refuses to run when struct copies with fixed-size buffers are broken (Mono's ARM64 JIT),
        /// since every bone and vertex matrix would silently come out as garbage.
        /// </summary>
        private static void CheckRuntime()
        {
            Matrix scale = Matrix.ScaleMatrix(2.0f, 3.0f, 4.0f);
            Matrix copy = scale;
            if (copy[5] != 3.0f || copy[10] != 4.0f)
            {
                throw new PlatformNotSupportedException(
                    "this runtime miscopies matrices; on Mono ARM64, run with 'mono --interpreter'");
            }
        }

        private static int List(string path)
        {
            using (ResourceNode root = Open(path))
            {
                Print(root, 0);
            }

            return 0;
        }

        private static void Print(ResourceNode node, int depth)
        {
            Console.WriteLine($"{new string(' ', depth * 2)}{node.Name} [{node.GetType().Name}]");
            foreach (ResourceNode child in node.Children)
            {
                Print(child, depth + 1);
            }
        }

        private static int Export(string path, string outFolder, ExportOptions options)
        {
            int failures = 0;
            using (ResourceNode root = Open(path))
            {
                List<BRRESNode> archives = FindArchives(root).ToList();
                if (archives.Count == 0)
                {
                    throw new InvalidDataException($"{path} contains no BRRES archives");
                }

                foreach (BRRESNode archive in archives)
                {
                    // A lone BRRES exports straight into the output folder; nested ones get subfolders.
                    string folder = archive == root ? outFolder : Path.Combine(outFolder, archive.Name);
                    failures += ExportArchive(archive, folder, options);
                }
            }

            return failures == 0 ? 0 : 1;
        }

        private static IEnumerable<BRRESNode> FindArchives(ResourceNode node)
        {
            if (node is BRRESNode archive)
            {
                yield return archive;
                yield break;
            }

            // Only look inside containers; populating unrelated resources (e.g. effects) is wasted work.
            if (!(node is U8Node || node is U8FolderNode || node is ARCNode || node is MRGNode))
            {
                yield break;
            }

            foreach (ResourceNode child in node.Children)
            {
                foreach (BRRESNode found in FindArchives(child))
                {
                    yield return found;
                }
            }
        }

        /// <summary>
        /// Mirrors BRRESNode.ExportToFolder, but with a format per resource type, Maya .anim export
        /// without the model picker dialog, and per-entry error reporting instead of stopping.
        /// </summary>
        private static int ExportArchive(BRRESNode archive, string outFolder, ExportOptions options)
        {
            Directory.CreateDirectory(outFolder);
            int failures = 0;
            foreach (BRESGroupNode group in archive.Children.OfType<BRESGroupNode>())
            {
                string extension = GroupExtension(group.Type, options);
                if (extension == null || extension == SkipExtension)
                {
                    continue;
                }

                foreach (BRESEntryNode entry in group.Children.OfType<BRESEntryNode>())
                {
                    string outPath = Path.Combine(outFolder, entry.Name + extension);
                    try
                    {
                        if (entry is CHR0Node animation && extension == ".anim")
                        {
                            // CHR0Node.Export asks for the model in a dialog; pass it directly instead.
                            AnimFormat.Serialize(animation, outPath, FindAnimationModel(archive, options));
                        }
                        else
                        {
                            entry.Export(outPath);
                        }

                        Console.WriteLine(outPath);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"failed: {outPath}: {e.GetType().Name}: {e.Message}");
                        failures++;
                    }
                }
            }

            return failures;
        }

        private static string GroupExtension(BRESGroupNode.BRESGroupType type, ExportOptions options)
        {
            switch (type)
            {
                case BRESGroupNode.BRESGroupType.Textures:
                    return options.TextureExtension;
                case BRESGroupNode.BRESGroupType.Palettes:
                    // Palettes are baked into decoded images, so they're only exported next to raw textures.
                    return options.TextureExtension == ".tex0" ? ".plt0" : null;
                case BRESGroupNode.BRESGroupType.Models:
                    return options.ModelExtension;
                case BRESGroupNode.BRESGroupType.CHR0:
                    return options.AnimationExtension;
                case BRESGroupNode.BRESGroupType.CLR0:
                    return ".clr0";
                case BRESGroupNode.BRESGroupType.SRT0:
                    return ".srt0";
                case BRESGroupNode.BRESGroupType.SHP0:
                    return ".shp0";
                case BRESGroupNode.BRESGroupType.PAT0:
                    return ".pat0";
                case BRESGroupNode.BRESGroupType.VIS0:
                    return ".vis0";
                case BRESGroupNode.BRESGroupType.SCN0:
                    return ".scn0";
                default:
                    return null;
            }
        }

        private static MDL0Node FindAnimationModel(BRRESNode archive, ExportOptions options)
        {
            IEnumerable<MDL0Node> models = archive.Children.OfType<BRESGroupNode>()
                .SelectMany(group => group.Children.OfType<MDL0Node>());
            MDL0Node model = options.AnimationModel == null
                ? models.FirstOrDefault()
                : models.FirstOrDefault(m => m.Name == options.AnimationModel);
            if (model == null)
            {
                throw new InvalidDataException(options.AnimationModel == null
                    ? "Maya .anim export needs a model in the same BRRES"
                    : $"no model named {options.AnimationModel}");
            }

            return model;
        }
    }
}
