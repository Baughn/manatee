using System.Globalization;
using System.Text;
using CsCheck;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle;

/// <summary>
/// Property test: the rawfile parser recovers exactly the variable/point
/// matrix that was formatted into ASCII rawfile syntax. This is the CsCheck
/// smoke test and a real guard on the parser at once.
/// </summary>
public class RawFileParserProperties
{
    [Fact]
    public void Parser_recovers_generated_rawfiles()
    {
        var gen =
            from variableCount in Gen.Int[1, 6]
            from pointCount in Gen.Int[1, 10]
            from values in Gen.Double[-1e12, 1e12].Array[variableCount * pointCount]
            select (variableCount, pointCount, values);

        gen.Sample(input =>
        {
            var (variableCount, pointCount, values) = input;
            var text = FormatRawFile(variableCount, pointCount, values);
            var raw = RawFile.Parse(text);

            Assert.Equal(pointCount, raw.PointCount);
            for (var p = 0; p < pointCount; p++)
                for (var v = 0; v < variableCount; v++)
                    Assert.Equal(values[p * variableCount + v], raw.Get($"v(n{v})", p));
        });
    }

    private static string FormatRawFile(int variableCount, int pointCount, double[] values)
    {
        var sb = new StringBuilder();
        sb.Append("Title: generated\nDate: n/a\nPlotname: Operating Point\nFlags: real\n");
        sb.Append(CultureInfo.InvariantCulture, $"No. Variables: {variableCount}\n");
        sb.Append(CultureInfo.InvariantCulture, $"No. Points: {pointCount}\n");
        sb.Append("Variables:\n");
        for (var v = 0; v < variableCount; v++)
            sb.Append(CultureInfo.InvariantCulture, $"\t{v}\tv(n{v})\tvoltage\n");
        sb.Append("Values:\n");
        for (var p = 0; p < pointCount; p++)
            for (var v = 0; v < variableCount; v++)
            {
                var value = values[p * variableCount + v].ToString("G17", CultureInfo.InvariantCulture);
                sb.Append(v == 0
                    ? FormattableString.Invariant($"{p}\t{value}\n")
                    : FormattableString.Invariant($"\t{value}\n"));
            }
        return sb.ToString();
    }
}
