using FileGuard.Core.Rules;
using Xunit;

namespace FileGuard.Core.Tests;

public class PathRulesTests
{
    private readonly PathRules _rules = PathRules.Default;

    [Theory]
    [InlineData(@"D:\proj\node_modules\pkg\index.js")]
    [InlineData(@"D:\proj\.git\config")]
    [InlineData(@"D:\proj\dist\app.js")]
    [InlineData(@"D:\proj\bin\Debug\a.cs")]
    [InlineData(@"D:\proj\package-lock.json")]
    [InlineData(@"D:\proj\foo.lock")]
    [InlineData(@"D:\proj\.guard-rules.md.tmp")]
    [InlineData(@"D:\proj\~$doc.docx")]
    public void IgnoresKnownNoise(string path)
        => Assert.True(_rules.IsIgnoredPath(path, @"D:\proj"));

    [Theory]
    [InlineData(@"D:\proj\src\main.cs")]
    [InlineData(@"D:\proj\README.md")]
    [InlineData(@"D:\proj\sub\deep\file.ts")]
    public void KeepsSourceFiles(string path)
        => Assert.False(_rules.IsIgnoredPath(path, @"D:\proj"));

    [Theory]
    [InlineData(@"D:\proj\a.png", false)]
    [InlineData(@"D:\proj\a.exe", false)]
    [InlineData(@"D:\proj\a.dll", false)]
    [InlineData(@"D:\proj\a.zip", false)]
    [InlineData(@"D:\proj\a.cs", true)]
    [InlineData(@"D:\proj\a.json", true)]
    [InlineData(@"D:\proj\a.md", true)]
    public void TextFileWhitelist(string path, bool expected)
        => Assert.Equal(expected, _rules.IsTextFile(path));

    [Fact]
    public void IsUnder_HandlesCaseAndSeparator()
    {
        Assert.True(PathRules.IsUnder(@"D:\Proj\Src\a.cs", @"d:\proj\src"));
        Assert.True(PathRules.IsUnder(@"D:\proj\src", @"D:\proj\src"));
        Assert.False(PathRules.IsUnder(@"D:\proj\src2\a.cs", @"D:\proj\src"));
        Assert.False(PathRules.IsUnder(@"D:\proj\a.cs", @"D:\other"));
    }
}
