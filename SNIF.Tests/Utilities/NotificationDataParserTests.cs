using FluentAssertions;
using SNIF.Core.Utilities;

namespace SNIF.Tests.Utilities;

public class NotificationDataParserTests
{
    [Fact]
    public void MatchesMatchId_ReturnsTrue_ForExactMatch()
    {
        var result = NotificationDataParser.MatchesMatchId("{\"type\":\"message\",\"matchId\":\"match-1\"}", "match-1");

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesMatchId_ReturnsFalse_ForSubstringOnly()
    {
        var result = NotificationDataParser.MatchesMatchId("{\"type\":\"message\",\"matchId\":\"match-10\"}", "match-1");

        result.Should().BeFalse();
    }

    [Fact]
    public void MatchesMatchId_ReturnsFalse_ForInvalidJson()
    {
        var result = NotificationDataParser.MatchesMatchId("not-json", "match-1");

        result.Should().BeFalse();
    }
}