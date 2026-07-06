using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace Manatee.Core.Tests.Lessons;

/// <summary>
/// Discovers and parses the lesson corpus (lessons/README-schema.md). Each lesson
/// directory holds <c>circuit.txt</c> (Falstad netlist) and <c>lesson.md</c> whose
/// YAML front-matter carries the machine-readable expectations CI checks. The
/// front-matter parser is hand-rolled for exactly this schema (no YAML library) —
/// scalar keys plus one list of <c>{name, probe:[x,y], value, tol, [time]}</c> maps.
/// </summary>
internal static class LessonCorpus
{
    // Anchored to THIS source file so discovery works from the build output dir.
    private static readonly string CorpusDir = FindCorpus(ThisFile());

    private static string ThisFile([CallerFilePath] string path = "") => path;

    public static string Dir => CorpusDir;

    private static string FindCorpus(string anchor)
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(anchor)!);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "lessons");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        // Fall back to walking up from the test binary location.
        d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "lessons");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the 'lessons' corpus directory.");
    }

    /// <summary>Enumerate lesson directory names that carry both required files.</summary>
    public static IEnumerable<string> Discover()
    {
        var dirs = new List<string>(Directory.GetDirectories(CorpusDir));
        dirs.Sort(System.StringComparer.Ordinal);
        foreach (var dir in dirs)
        {
            if (File.Exists(Path.Combine(dir, "lesson.md")) && File.Exists(Path.Combine(dir, "circuit.txt")))
                yield return Path.GetFileName(dir);
        }
    }

    public static Lesson Load(string name)
    {
        var dir = Path.Combine(CorpusDir, name);
        var circuit = File.ReadAllText(Path.Combine(dir, "circuit.txt"));
        var md = File.ReadAllText(Path.Combine(dir, "lesson.md"));
        var lesson = ParseFrontMatter(md);
        lesson.Name = name;
        lesson.CircuitText = circuit;
        return lesson;
    }

    private static Lesson ParseFrontMatter(string md)
    {
        var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lesson = new Lesson();

        // Front-matter is between the first '---' and the next '---'.
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { start = i + 1; break; }
        }
        if (start < 0) throw new InvalidDataException("lesson.md has no front-matter block.");

        Expectation? cur = null;
        for (var i = start; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.Trim() == "---") break;
            if (raw.Trim().Length == 0) continue;

            var indent = CountIndent(raw);
            var line = raw.Trim();

            if (indent == 0)
            {
                var (key, val) = SplitKv(line);
                switch (key)
                {
                    case "lesson": lesson.Number = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "slug": lesson.Slug = val; break;
                    case "title": lesson.Title = val; break;
                    case "circuit": lesson.Circuit = val; break;
                    case "analysis": lesson.Analysis = val; break;
                    case "stop": lesson.Stop = ParseD(val); break;
                    case "expectations": break;   // list follows
                }
                continue;
            }

            // List item: "- name: ..." begins a new expectation.
            if (line.StartsWith("- "))
            {
                cur = new Expectation();
                lesson.Expectations.Add(cur);
                line = line.Substring(2).Trim();
            }
            if (cur is null) continue;

            var (k, v) = SplitKv(line);
            switch (k)
            {
                case "name": cur.Name = v; break;
                case "probe": ParseProbe(v, cur); break;
                case "value": cur.Value = ParseD(v); break;
                case "tol": cur.Tol = ParseD(v); break;
                case "time": cur.Time = ParseD(v); break;
            }
        }
        return lesson;
    }

    private static int CountIndent(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }

    private static (string key, string val) SplitKv(string line)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return (line, string.Empty);
        var key = line.Substring(0, idx).Trim();
        var val = line.Substring(idx + 1).Trim();
        return (key, val);
    }

    private static void ParseProbe(string v, Expectation e)
    {
        // "[0, 128]"
        var inner = v.Trim().TrimStart('[').TrimEnd(']');
        var parts = inner.Split(',');
        e.ProbeX = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
        e.ProbeY = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
    }

    private static double ParseD(string v)
        => double.Parse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
}

internal sealed class Lesson
{
    public string Name = "";
    public int Number;
    public string Slug = "";
    public string Title = "";
    public string Circuit = "";
    public string Analysis = "dc";
    public double? Stop;
    public string CircuitText = "";
    public List<Expectation> Expectations = new();

    public bool IsTransient => string.Equals(Analysis, "transient", System.StringComparison.OrdinalIgnoreCase);
}

internal sealed class Expectation
{
    public string Name = "";
    public int ProbeX, ProbeY;
    public double Value;
    public double Tol;
    public double? Time;
}
