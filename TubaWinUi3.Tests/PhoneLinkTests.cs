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

    [Fact]
    public void UploadLimit_IsTwentyGigabytes() =>
        Assert.Equal(20L * 1024 * 1024 * 1024, PhoneLinkService.MaxUploadBytes);

    [Fact]
    public void CancelJob_UnknownId_ReturnsFalse() => Assert.False(PhoneLinkService.CancelJob("not-a-job"));

    // ───────────────────────── Toast 点击参数解析 ─────────────────────────

    [Fact]
    public void ToastHandlerArgs_ParsesKeyValuePairs()
    {
        var (action, target, job) = PhoneLinkNotifier.ParseHandlerArgs(new[] { "action=phone-link", "target=jobs", "job=abc123" });
        Assert.Equal("phone-link", action);
        Assert.Equal("jobs", target);
        Assert.Equal("abc123", job);
    }

    [Fact]
    public void ToastHandlerArgs_DefaultsAndQuotes()
    {
        var (action, target, job) = PhoneLinkNotifier.ParseHandlerArgs(new[] { "action=phone-link", "target=chat" });
        Assert.Equal("phone-link", action);
        Assert.Equal("chat", target);
        Assert.Null(job);

        (action, target, job) = PhoneLinkNotifier.ParseHandlerArgs(new[] { "\"action=phone-link\"", "TARGET=CHAT" });
        Assert.Equal("phone-link", action);
        Assert.Equal("CHAT", target);
        Assert.Null(job);
    }

    [Fact]
    public void ToastHandlerArgs_RecognizesActiveInterceptFallback()
    {
        var (action, _, _) = PhoneLinkNotifier.ParseHandlerArgs(new[] { "-ToastActivated", "show-active-intercept" });
        Assert.Equal("show-active-intercept", action);
    }

    [Fact]
    public void ToastArgumentString_ParsesToolkitFormat()
    {
        var (action, target, job) = PhoneLinkNotifier.ParseArgumentString("action=phone-link&target=chat&job=xyz");
        Assert.Equal("phone-link", action);
        Assert.Equal("chat", target);
        Assert.Equal("xyz", job);

        (action, target, job) = PhoneLinkNotifier.ParseArgumentString(null);
        Assert.Equal("", action);
        Assert.Equal("", target);
        Assert.Null(job);
    }

    [Fact]
    public void Truncate_AddsEllipsis()
    {
        Assert.Equal("abc", PhoneLinkNotifier.Truncate("abc", 5));
        Assert.Equal("ab…", PhoneLinkNotifier.Truncate("abcdef", 2));
        Assert.Equal("", PhoneLinkNotifier.Truncate("", 3));
    }

    // ───────────────────────── 传输速度 ─────────────────────────

    [Fact]
    public void TransferReporter_InstantAndAverageSpeed()
    {
        var now = 0L;
        var events = new List<PhoneTransferProgress>();
        var reporter = new TransferSpeedReporter("t1", "a.bin", PhoneTransferDirection.Upload, 1000,
            e => events.Add(e), () => now, minIntervalMs: 100);

        now = 1000;
        reporter.Report(100); // 100 B / 1 s
        Assert.Single(events);
        Assert.Equal(100, events[0].BytesPerSecond, 3);
        Assert.Equal(1000, events[0].TotalBytes);
        Assert.Equal(PhoneTransferDirection.Upload, events[0].Direction);

        now = 1100;
        reporter.Report(150, force: true); // 50 B / 0.1 s
        Assert.Equal(500, events[1].BytesPerSecond, 3);

        now = 2000;
        reporter.Complete(1000); // 全程平均：1000 B / 2 s
        Assert.True(events[2].Completed);
        Assert.Equal(500, events[2].BytesPerSecond, 3);
    }

    [Fact]
    public void TransferReporter_ThrottlesAndStopsAfterFinish()
    {
        var now = 0L;
        var count = 0;
        var reporter = new TransferSpeedReporter("t2", "b.bin", PhoneTransferDirection.Download, -1,
            _ => count++, () => now, minIntervalMs: 200);

        now = 250;
        reporter.Report(10);
        Assert.Equal(1, count);

        now = 300;
        reporter.Report(20); // 距上次 50ms，被节流
        Assert.Equal(1, count);

        reporter.Fail(20);
        Assert.Equal(2, count);

        reporter.Report(30, force: true); // 结束后不再上报
        Assert.Equal(2, count);
    }

    [Fact]
    public void FormatSpeed_UsesReadableUnits()
    {
        Assert.Equal("512 B/s", PhoneTransferProgress.FormatSpeed(512));
        Assert.Equal("1.5 KB/s", PhoneTransferProgress.FormatSpeed(1536));
        Assert.Equal("2.0 MB/s", PhoneTransferProgress.FormatSpeed(2 * 1024 * 1024));
    }
}
