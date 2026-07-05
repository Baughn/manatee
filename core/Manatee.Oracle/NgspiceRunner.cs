using System.Diagnostics;
using System.Text;

namespace Manatee.Oracle;

/// <summary>
/// Drives ngspice in batch mode over a SPICE deck and returns the parsed
/// ASCII rawfile. ngspice is a dev/CI dependency pinned by the devshell;
/// per testing-strategy.md, its absence is a hard failure, never a skip.
/// </summary>
public sealed class NgspiceRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Assembles a full batch deck: title, netlist body, and a .control
    /// block that runs the given analyses and writes an ASCII rawfile.
    /// </summary>
    public static string AssembleDeck(string title, string netlist, IReadOnlyList<string> analysisCommands)
    {
        var deck = new StringBuilder();
        deck.Append("* ").Append(title).Append('\n');
        deck.Append(netlist.Trim()).Append('\n');
        deck.Append(".control\n");
        deck.Append("set filetype=ascii\n");
        foreach (var command in analysisCommands)
            deck.Append(command).Append('\n');
        deck.Append("write output.raw\n");
        deck.Append(".endc\n");
        deck.Append(".end\n");
        return deck.ToString();
    }

    /// <summary>Runs the netlist through ngspice and parses the result.</summary>
    public RawFile Run(string title, string netlist, params string[] analysisCommands)
    {
        var workDir = Directory.CreateTempSubdirectory("manatee-oracle-").FullName;
        try
        {
            var deckPath = Path.Combine(workDir, "deck.cir");
            File.WriteAllText(deckPath, AssembleDeck(title, netlist, analysisCommands));

            var psi = new ProcessStartInfo
            {
                FileName = "ngspice",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(deckPath);

            Process process;
            try
            {
                process = Process.Start(psi)
                    ?? throw new InvalidOperationException("ngspice failed to start.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new InvalidOperationException(
                    "ngspice not found on PATH. Oracle tests hard-fail rather than skip; " +
                    "enter the devshell (`nix develop`) to get the pinned ngspice.", e);
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    throw new TimeoutException($"ngspice did not finish within {Timeout.TotalSeconds}s.");
                }
                process.WaitForExit(); // flush async output after the timed wait

                var rawPath = Path.Combine(workDir, "output.raw");
                if (process.ExitCode != 0 || !File.Exists(rawPath))
                {
                    throw new InvalidOperationException(
                        $"ngspice exited with {process.ExitCode} and " +
                        $"{(File.Exists(rawPath) ? "a" : "no")} rawfile.\n" +
                        $"stdout:\n{stdout.Result}\nstderr:\n{stderr.Result}");
                }

                return RawFile.Parse(File.ReadAllText(rawPath));
            }
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
