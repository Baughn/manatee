using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle;

/// <summary>
/// Verify (snapshot) smoke test: pins the exact deck text the oracle runner
/// emits, so deck-emission changes show up as reviewable snapshot diffs.
/// </summary>
public class DeckAssemblyTests
{
    [Fact]
    public Task Deck_format_is_stable()
    {
        var deck = NgspiceRunner.AssembleDeck(
            "voltage divider",
            """
            V1 in 0 DC 10
            R1 in out 1k
            R2 out 0 1k
            """,
            ["op"]);

        return Verify(deck);
    }
}
