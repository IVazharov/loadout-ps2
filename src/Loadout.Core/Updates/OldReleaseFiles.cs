using System.Security.Cryptography;

namespace Loadout.Updates;

/// <summary>
/// The licence files that Loadout's Windows zips 0.9.0 to 0.10.0 put beside the program. Since 0.11.0 every licence text
/// is on the About page and the zip holds only the program and its readme, but an update replaces only the files the new
/// zip has, so these stayed behind, outdated. A file goes only when its contents are exactly what one of those zips held,
/// so a file of the same name that belongs to something else stays; a folder goes only when that leaves it empty.
/// </summary>
public static class OldReleaseFiles
{
    /// <summary>Each file's path beside the program, with the SHA-256 of every version the zips shipped.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Licences = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["LICENSE"] = ["e3e5cb556bea12729907fd9ce0eead2ebd5618a2ad027746cc4c7843749b85b2"],
        ["THIRD-PARTY-NOTICES.txt"] = ["d3340e390fb2c5a900b7e46484d676d470196a35185293903decb30f934ad723", "e7c1376f03b21aec9b708c810fbe50295a58fa99c8a19ebde700e7f63f403829"],
        ["third_party/README.md"] = ["00efdde254d6fb813ff3770bda2a89e3c62b27330065103d7881af0e6941e067"],
        ["third_party/barlow/OFL.txt"] = ["186d750eb496a4c17a76385f82be6aea2ac1cf2de074a811d63786cf374ea73f"],
        ["third_party/barlow/README.md"] = ["9d6c77877324a7a7c5e2ac882a85fc3715a1ddcd9bc46805c1f8b539a78a5521"],
        ["third_party/MameChd/LICENSE"] = ["1d474bf59ecea2d2efb5228eb5b6389557b9901fe44eb50d455556d8486489f6"],
        ["third_party/MameChd/README.md"] = ["3d870f5b5ba419036727ef34af1fa927de517e86a338af36e984853b9eed78aa"],
        ["third_party/OplVmcGroups/LICENSE"] = ["0a6b37ee9a3e3778cd8578ce050a7919f28345102115359bb2fcd71c1d1f579d"],
        ["third_party/OplVmcGroups/README.md"] = ["4efb3e25f02b99e31aa6d2056fdc544778f7c372f93dd88596ab05a19e15cb2f"],
        ["third_party/OplVmcGroups/vmc-groups.txt"] = ["72c7e7fd553c5b3e3239f168bfc8378cb67ae26d83f44e21adfac87a1613d0df"],
        ["third_party/ps2sdk-apa/LICENSE"] = ["1ecee940922a6886baccddd9133d17f1ce677d32c5a954fac8e48224f2766fe8"],
        ["third_party/ps2sdk-apa/Makefile"] = ["3b4baec55587adce7f5ed7b6ed19db467cb602a0d4b304e53b700f2d73cd9cde"],
        ["third_party/ps2sdk-apa/README.md"] = ["d3ee156c898cad669875944b5aae1e6cf11e4bebab745a69f1a214df0a0e9c36"],
        ["third_party/ps2sdk-elf-loader2/elf_loader_common.h"] = ["1658296c78da01b059b1b22e1cb09ebcab1d4f178e75696c2b8d09d86d9dcea9"],
        ["third_party/ps2sdk-elf-loader2/LICENSE"] = ["1ecee940922a6886baccddd9133d17f1ce677d32c5a954fac8e48224f2766fe8"],
        ["third_party/ps2sdk-elf-loader2/README.md"] = ["8b277710fc47595e7b1f87988ce88b95101065d65c4d58959c5a4a0e1fe7e46f"],
        ["third_party/ps2sdk-netman/LICENSE"] = ["1ecee940922a6886baccddd9133d17f1ce677d32c5a954fac8e48224f2766fe8"],
        ["third_party/ps2sdk-netman/README.md"] = ["f1d5f367525481b04248e80a60578ffd1482a144df6fe9d07fc4c3d3d71beb7f"],
        ["third_party/Puff/LICENSE"] = ["4e196fbeb5aef57e1886c40b55fcc8bac4193969b65fe46b33e4a3036249805b"],
        ["third_party/Puff/README.md"] = ["1406f797f015ba07fd4e4317e348e633a788730f4357f0db7b8ca1fc4a06929b"],
        ["third_party/ZstdSharp/LICENSE"] = ["091f6712354d37db0028dba373e2276e077f3b4d959526b899a8807dcc9728cc"],
        ["third_party/ZstdSharp/README.md"] = ["69e73bc6937f2b85d98e1d5a34a2b9e7064b37935af4033b515e7c773e9df7d6"],
        ["third_party/ps2sdk-apa/src/apa-opt.h"] = ["e85d3ddd1bf020e9b4ff52e06b1564aba6410c12fcdac4ae7c79081a6de2c212"],
        ["third_party/ps2sdk-apa/src/apa.c"] = ["3bbab327115b78900a0e52db03372f7d22661fdec830266f7e04ca8970ac186d"],
        ["third_party/ps2sdk-apa/src/cache.c"] = ["279e763b2b6522fc22f442d8b48318a84f75ab1bbcaba395c83c8022dcb128b6"],
        ["third_party/ps2sdk-apa/src/free.c"] = ["e02621423cadc154a3e37648875475e6278565fcce748e01bc9def3ae5a29211"],
        ["third_party/ps2sdk-apa/src/hdd.c"] = ["6e38ed82483fabe9a890e71e0d871863cefcdf544649bff2131c493da4c0f66a"],
        ["third_party/ps2sdk-apa/src/hdd.h"] = ["a14dd40ac6c5e0130cb465fdca6bf40f6c5ef5d527699a70611dba2e77498f49"],
        ["third_party/ps2sdk-apa/src/hdd_blkio.c"] = ["129a5b9da7476defa939df6c785769462bf0526df61a8cd060e774b29087ff48"],
        ["third_party/ps2sdk-apa/src/hdd_blkio.h"] = ["91941adff0b57771dba0915a99bc71ca8ceaf15414959c53d583847df63d6f13"],
        ["third_party/ps2sdk-apa/src/hdd_fio.c"] = ["913be6ce5b25156a65be3ab5a417a80b69a5e576c8c9d9ae0d2af3d77b71d40f"],
        ["third_party/ps2sdk-apa/src/hdd_fio.h"] = ["0e898f76d5fb1ba24fe073adf02da58c6990efc39bfd60590c1f03c988f0b23e"],
        ["third_party/ps2sdk-apa/src/imports.lst"] = ["c346760baf32475ad01051fa9aea6173a85297d1d034522658f1c3b79d4c06ab"],
        ["third_party/ps2sdk-apa/src/irx_imports.h"] = ["3d41e52c3021ea67e06a593f81e269fb8784cef0592fdfc93806d196c2a97637"],
        ["third_party/ps2sdk-apa/src/journal.c"] = ["edb6991319a8f8876c63ba4bbab846132978b0484e7223c33acc716832eae869"],
        ["third_party/ps2sdk-apa/src/libapa.h"] = ["a5ffc59ef1e39d17e8c6bbe3577233d0147bf6d46348fdcef87e93b68a386690"],
        ["third_party/ps2sdk-apa/src/misc.c"] = ["606a4847c7afe74c0cac8f02abb7871e8842277e847a20353b86c6513fb006c6"],
        ["third_party/ps2sdk-apa/src/password.c"] = ["a373b351290208f8a27b89411e62c0a9d3c19028b127925dcd184e37c76deea7"],
        ["third_party/ps2sdk-netman/src/netman.c"] = ["2729f523570606cb6dd229907a753c3f830589f69bd10eaddb9c11f2c7d29b82"],
        ["third_party/ps2sdk-netman/src/rpc_client.c"] = ["0b18c97ec0b6da7b5eab3f85026b888608ad3a9dde36a0e40b9070137d02964b"],
        ["third_party/ps2sdk-netman/src/rpc_server.c"] = ["487f0c6f05b16fbe7b404b3bd4a2671ba8338ca807eb13994676c8c931e162c0"],
        ["third_party/ps2sdk-netman/src/include/internal.h"] = ["b34fe61fdd8cb7e1c1398ed4709e30944d217f47580ef231f879ad0620b5ee37"],
        ["third_party/ps2sdk-netman/src/include/rpc_client.h"] = ["60eb2e37eb93d0a4b3b64e07d8380d443d74839fe9ff4453c20abb5dcad39c1c"],
        ["third_party/ps2sdk-netman/src/include/rpc_server.h"] = ["c6b461214919c64f5dbfb8ec54e68ff4f32d7f34cd11ef7d27b4b92b1752ed9d"],
    };

    /// <summary>Deletes the old licence files in <paramref name="appFolder"/> and returns the paths deleted.</summary>
    public static IReadOnlyList<string> Remove(string appFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(appFolder);
        var removed = new List<string>();
        foreach (var (relative, hashes) in Licences)
        {
            var path = Path.Combine(appFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(path) && hashes.Contains(Sha256(path), StringComparer.Ordinal))
                {
                    File.Delete(path);
                    removed.Add(relative);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next start.
            }
        }

        // The folders those files were in, deepest first, each only when nothing else is in it.
        var folders = Licences.Keys
            .Select(relative => Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(folder => !string.IsNullOrEmpty(folder))
            .SelectMany(folder => Parents(folder!))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(folder => folder.Length);
        foreach (var folder in folders)
        {
            var path = Path.Combine(appFolder, folder);
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left as it is.
            }
        }

        return removed;
    }

    private static IEnumerable<string> Parents(string folder)
    {
        for (var current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            yield return current;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
