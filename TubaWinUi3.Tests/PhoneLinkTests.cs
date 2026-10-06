using TubaWinUi3.Services;
using Xunit;

namespace TubaWinUi3.Tests;

public class PhoneLinkTests
{
    [Theory]
    [InlineData("Microsoft.PowerToys", true)]
    [InlineData("7zip.7zip", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("x; calc", false)]
    [InlineData("x\" --force", false)]
    public void WingetId_Validation(string id, bool ok) => Assert.Equal(ok, PhoneLinkService.IsValidWingetId(id));

    [Theory]
    [InlineData("..\\..\\evil.exe", "evil.exe")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    public void SanitizeFileName_StripsPaths(string? input, string expected) =>
        Assert.Equal(expected, PhoneLinkService.SanitizeFileName(input));

    [Fact]
    public void PairCode_IsSixDigits() => Assert.Matches(@"^\d{6}$", PhoneLinkService.NewPairCode());

    [Fact]
    public void Pair_WrongCode_Rejected()
    {
        var (token, error) = PhoneLinkService.TryPair("not-the-code", "test");
        Assert.Null(token);
        Assert.NotNull(error);
        PhoneLinkService.RevokeAll();
    }

    [Fact]
    public void Pair_CorrectCode_IssuesToken()
    {
        var (token, error) = PhoneLinkService.TryPair(PhoneLinkService.PairCode, "test");
        Assert.NotNull(token);
        Assert.Null(error);
        PhoneLinkService.RevokeAll();
    }

    [Fact]
    public void QrPayload_ContainsAddressAndCode()
    {
        var s = PhoneLinkService.BuildQrPayload("192.168.1.2");
        Assert.StartsWith("tubalink://192.168.1.2:", s);
        Assert.Contains("code=" + PhoneLinkService.PairCode, s);
    }
}
