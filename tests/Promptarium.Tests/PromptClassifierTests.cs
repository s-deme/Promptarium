using Promptarium.Services;
using Xunit;

namespace Promptarium.Tests;

public sealed class PromptClassifierTests
{
    [Theory]
    [InlineData(" , (red, [blue, {green, gold}]), , 夜空  🌟, ", "(red, [blue, {green, gold}])|夜空  🌟")]
    [InlineData("first), second, (third, fourth", "first)|second|(third, fourth")]
    [InlineData("first, second", "first|second")]
    [InlineData(" , \t, ", "")]
    [InlineData(null, "")]
    public void Classify_preserves_token_boundaries_and_text(string? prompt, string expected)
    {
        var tags = new PromptClassifier().Classify(prompt);

        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), tags.Select(tag => tag.RawText));
        Assert.Equal(Enumerable.Range(0, tags.Count), tags.Select(tag => tag.Ordinal));
    }
}
