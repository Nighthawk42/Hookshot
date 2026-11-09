using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace HookshotBuilder.Launcher;

/// What the launcher reads from a game folder besides the options: the graphics cards the last run saw, the language, the
/// version, whether the game is running
static partial class GameFolder
{
    /// The cards the game listed in its last run ("vk: device <n>: <name>, Vulkan ..." in hookshot_log.txt or the run before);
    /// the GPU device row's choice n+1 is card n. Empty before the first run.
    public static List<string> Gpus(string folder)
    {
        foreach (var name in new[] { "hookshot_log.txt", "hookshot_log.1.txt", "hookshot_log.2.txt" })
        {
            var found = new SortedDictionary<int, string>();
            try
            {
                using var reader = new StreamReader(new FileStream(Path.Combine(folder, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                // (the cards are listed in the first moments of a run: the head of a large log is enough)
                for (var i = 0; i < 4000 && reader.ReadLine() is { } line; i++)
                    if (DeviceLine().Match(line) is { Success: true } m) found[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.Trim();
            }
            catch (Exception) { continue; }
            if (found.Count > 0) return found.Values.ToList();
        }
        return new List<string>();
    }

    [GeneratedRegex(@"vk: device (\d+): ([^,]+),")]
    private static partial Regex DeviceLine();

    public sealed record LanguageChoice(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    /// language.txt: the code on its first line that is not a comment, and the codes the file lists ("# en-US    English (US)")
    public static (string? Current, List<LanguageChoice> All) Language(string folder)
    {
        var all = new List<LanguageChoice>();
        string? current = null;
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(folder, "language.txt")))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith('#'))
                {
                    if (ListedLanguage().Match(t) is { Success: true } m) all.Add(new LanguageChoice(m.Groups[1].Value, m.Groups[2].Value.Trim()));
                }
                else current ??= t;
            }
        }
        catch (Exception) { }
        return (current, all);
    }

    [GeneratedRegex(@"^#\s+([A-Za-z]{2,3}(?:-[A-Za-z0-9]+)?)\s{2,}(.+)$")]
    private static partial Regex ListedLanguage();

    /// The code line replaced; the comments stay as the builder wrote them
    public static void SetLanguage(string folder, string code)
    {
        var path = Path.Combine(folder, "language.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var i = lines.FindIndex(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        if (i >= 0) lines[i] = code;
        else lines.Insert(Math.Min(1, lines.Count), code);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    /// The game started from this folder is running (its options file is then the in-game menu's to write)
    public static bool Running(string folder)
    {
        var exe = MainWindow.GameExe(folder);
        foreach (var p in Process.GetProcessesByName("hookshot"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception) { return true; }   // (not ours to read: a hookshot is running, likely this one)
            finally { p.Dispose(); }
        }
        return false;
    }

    public static string SaveFolder(string folder) => Path.Combine(folder, "user");
}
