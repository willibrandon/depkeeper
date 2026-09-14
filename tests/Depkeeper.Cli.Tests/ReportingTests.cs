namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that diagnostic reports redact runtime credentials and escape active markup.
/// </summary>
[TestClass]
public sealed class ReportingTests
{
    /// <summary>
    /// Redacts known credentials in both summaries and details.
    /// </summary>
    [TestMethod]
    public void ReportsDoNotExposeCredentialsOrRawHtml()
    {
        var redactor = new Redactor("example-credential");
        var report = ReportWriter.Format(
            [new ReportEntry("owner/repository", 1, "blocked", "example-credential <script>alert(1)</script>")],
            "auto", false, redactor);
        Assert.DoesNotContain("example-credential", report);
        Assert.DoesNotContain("<script>", report);
        Assert.Contains("REDACTED", report);
        Assert.Contains("https://github.com/owner/repository/pull/1", report);
    }
}
