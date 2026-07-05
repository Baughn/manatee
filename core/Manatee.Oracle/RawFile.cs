using System.Globalization;

namespace Manatee.Oracle;

/// <summary>
/// A parsed ngspice ASCII rawfile: variable names plus a value matrix
/// indexed [point][variable]. Real-valued plots only for now; AC/complex
/// support arrives with the phasor work if it ever does.
/// </summary>
public sealed class RawFile
{
    private readonly string[] _variables;
    private readonly double[][] _points;

    private RawFile(string[] variables, double[][] points)
    {
        _variables = variables;
        _points = points;
    }

    public IReadOnlyList<string> Variables => _variables;
    public int PointCount => _points.Length;

    /// <summary>Value of a variable (e.g. "v(out)", "i(v1)") at a point. Case-insensitive.</summary>
    public double Get(string variable, int point = 0)
    {
        for (var i = 0; i < _variables.Length; i++)
            if (string.Equals(_variables[i], variable, StringComparison.OrdinalIgnoreCase))
                return _points[point][i];
        throw new KeyNotFoundException(
            $"Variable '{variable}' not in rawfile. Available: {string.Join(", ", _variables)}");
    }

    public static RawFile Parse(string text)
    {
        var lines = text.Split('\n');
        var lineIndex = 0;

        int variableCount = -1, pointCount = -1;
        var isComplex = false;

        // Header: "Key: value" lines up to "Variables:".
        for (; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.StartsWith("Flags:", StringComparison.OrdinalIgnoreCase))
                isComplex = line.Contains("complex", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("No. Variables:", StringComparison.OrdinalIgnoreCase))
                variableCount = int.Parse(line["No. Variables:".Length..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("No. Points:", StringComparison.OrdinalIgnoreCase))
                pointCount = int.Parse(line["No. Points:".Length..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("Variables:", StringComparison.OrdinalIgnoreCase))
                break;
        }

        if (isComplex)
            throw new NotSupportedException("Complex (AC small-signal) rawfiles are not supported yet.");
        if (variableCount < 1 || pointCount < 1)
            throw new FormatException("Rawfile header missing 'No. Variables' or 'No. Points'.");

        // Variable table: "\t<index>\t<name>\t<type>".
        var variables = new string[variableCount];
        lineIndex++;
        for (var v = 0; v < variableCount; v++, lineIndex++)
        {
            var fields = lines[lineIndex].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            variables[v] = fields[1];
        }

        // "Values:" marker, then per point: "<pointIndex>\t<value>" followed by
        // one indented value per remaining variable.
        while (lineIndex < lines.Length && !lines[lineIndex].TrimStart().StartsWith("Values:", StringComparison.OrdinalIgnoreCase))
            lineIndex++;
        lineIndex++;

        var points = new double[pointCount][];
        for (var p = 0; p < pointCount; p++)
        {
            points[p] = new double[variableCount];
            for (var v = 0; v < variableCount; v++, lineIndex++)
            {
                var fields = lines[lineIndex].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // First line of a point carries the point index as field 0.
                var valueField = v == 0 ? fields[1] : fields[0];
                points[p][v] = double.Parse(valueField, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            // Points may be separated by a blank line.
            while (lineIndex < lines.Length && lines[lineIndex].Trim().Length == 0)
                lineIndex++;
        }

        return new RawFile(variables, points);
    }
}
