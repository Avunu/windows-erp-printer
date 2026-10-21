using System.Text.Json.Nodes;
using ErpPrinter.Outbox;
using ErpPrinter.Templating;

namespace ErpPrinter.Core.Tests;

/// <summary>Fixed clock and time zone, so date tokens are deterministic.</summary>
public sealed class TemplateFixedTime(DateTimeOffset utcNow, TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test", offset, "Test", "Test");
}

public class TemplateCleanTitleTests
{
    [Theory]
    [InlineData("Microsoft Word - Invoice 1042.docx", "Invoice 1042")]
    [InlineData("Quote.xlsx", "Quote")]
    [InlineData("Report v1.2", "Report v1.2")]
    [InlineData("https://example.com/page", "https://example.com/page")]
    [InlineData("   ", "Untitled")]
    [InlineData("", "Untitled")]
    [InlineData(null, "Untitled")]
    [InlineData("MICROSOFT EXCEL - Budget.XLSX", "Budget")]
    [InlineData("Notes.txt.pdf", "Notes.txt")]
    public void Cleans_titles(string? title, string expected) => Assert.Equal(expected, Template.CleanTitle(title));
}

public class TemplateExpandTests
{
    private static readonly OutboxMeta Meta = new()
    {
        Id = "abc123",
        Profile = "Send to Odoo",
        User = @"CORP\jdoe",
        Title = "Microsoft Word - Offer \"Q3\".docx",
        Computer = "PC-01",
        JobId = "17",
        Printed = DateTimeOffset.Parse("2026-03-04T05:06:07.0000000+01:00"),
    };

    [Fact]
    public void Expands_all_tokens()
    {
        var tokens = TemplateTokens.Create(Meta);
        Assert.Equal(
            @"Offer ""Q3""|CORP\jdoe|jdoe|CORP|PC-01|17|abc123|Send to Odoo",
            Template.Expand("{title}|{user}|{username}|{domain}|{computer}|{jobid}|{id}|{profile}", tokens));
    }

    [Fact]
    public void Formats_dates_from_the_print_time()
    {
        var tokens = TemplateTokens.Create(Meta);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", tokens["date"]);
        Assert.Matches(@"^\d{6}$", tokens["time"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{6}$", tokens["datetime"]);
    }

    [Fact]
    public void Shows_the_print_time_in_the_local_time_zone()
    {
        var time = new TemplateFixedTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), TimeSpan.FromHours(-5));
        var tokens = TemplateTokens.Create(Meta, time); // 2026-03-04 04:06:07 UTC
        Assert.Equal("2026-03-03", tokens["date"]);
        Assert.Equal("230607", tokens["time"]);
        Assert.Equal("2026-03-03 230607", tokens["datetime"]);
    }

    [Fact]
    public void Uses_the_current_time_when_the_document_has_no_print_time()
    {
        var time = new TemplateFixedTime(DateTimeOffset.Parse("2030-05-06T07:08:09Z"), TimeSpan.FromHours(2));
        var tokens = TemplateTokens.Create(new OutboxMeta { Title = "x", User = "u", Printed = null }, time);
        Assert.Equal("2030-05-06", tokens["date"]);
        Assert.Equal("090809", tokens["time"]);
    }

    [Fact]
    public void Splits_UPN_style_user_names()
    {
        var tokens = TemplateTokens.Create(new OutboxMeta { User = "jdoe@corp.example", Title = "x", Printed = null });
        Assert.Equal("jdoe", tokens["username"]);
        Assert.Equal("corp.example", tokens["domain"]);
    }

    [Fact]
    public void Leaves_plain_user_names_whole()
    {
        var tokens = TemplateTokens.Create(new OutboxMeta { User = "jdoe", Title = "x" });
        Assert.Equal("jdoe", tokens["username"]);
        Assert.Equal("", tokens["domain"]);
    }

    [Fact]
    public void Tolerates_sidecars_with_missing_fields()
    {
        var tokens = TemplateTokens.Create(new OutboxMeta { Computer = null });
        Assert.Equal("Untitled", tokens["title"]);
        Assert.Equal("", tokens["computer"]);
    }

    [Fact]
    public void Leaves_unknown_tokens_alone_and_is_case_insensitive() =>
        Assert.Equal("A-{nope}", Template.Expand("{TITLE}-{nope}", new Dictionary<string, string> { ["title"] = "A" }));

    [Fact]
    public void Escapes_values_for_JSON()
    {
        var json = Template.Expand("""{"description": "{title} by {user}"}""", TemplateTokens.Create(Meta), jsonEscape: true);
        Assert.Equal(@"Offer ""Q3"" by CORP\jdoe", (string?)JsonNode.Parse(json)!["description"]);
    }

    [Fact]
    public void Does_not_escape_without_the_option() =>
        Assert.Equal(@"CORP\jdoe", Template.Expand("{user}", TemplateTokens.Create(Meta)));
}

public class TemplateJsonObjectTests
{
    [Fact]
    public void Parses_objects() =>
        Assert.Equal(3, (int)Template.ParseJsonObject("""  {"a": 3}""")["a"]!);

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("[{\"a\": 1}]")]
    [InlineData("5")]
    [InlineData("")]
    [InlineData("""{"partner_id": {id}}""")]
    [InlineData("""{"a": 1""")]
    public void Rejects_everything_else_with_a_short_reason(string text) =>
        Assert.False(string.IsNullOrEmpty(Assert.Throws<FormatException>(() => Template.ParseJsonObject(text)).Message));

    [Fact]
    public void Names_the_missing_brace_for_arrays() =>
        Assert.Equal("the text does not start with {.", Assert.Throws<FormatException>(() => Template.ParseJsonObject("[{}]")).Message);
}

public class TemplateDocumentFileNameTests
{
    [Fact]
    public void Builds_a_safe_PDF_file_name() =>
        Assert.Equal("a_b_c______.pdf", DocumentFileName.Build("{title}", new OutboxMeta { Title = "a/b:c*?\"<>|.pdf", User = "u" }));

    [Fact]
    public void Truncates_long_names_and_never_ends_up_empty()
    {
        var meta = new OutboxMeta { Title = "" };
        Assert.Equal(154, DocumentFileName.Build(new string('x', 400), meta).Length);
        Assert.Equal("Untitled.pdf", DocumentFileName.Build("...", meta));
        Assert.Equal("Untitled.pdf", DocumentFileName.Build("", meta));
    }

    [Fact]
    public void Supports_combined_templates() =>
        Assert.Equal("jdoe - Invoice.pdf", DocumentFileName.Build("{username} - {title}", new OutboxMeta { Title = "Invoice", User = @"CORP\jdoe", Printed = DateTimeOffset.Parse("2026-01-02T03:04:05Z") }));

    [Fact]
    public void Does_not_double_the_pdf_extension() =>
        Assert.Equal("report.pdf", DocumentFileName.Build("report.PDF", new OutboxMeta { Title = "x" }));

    [Fact]
    public void Trims_the_name_after_truncating()
    {
        var name = DocumentFileName.Build(new string('x', 149) + " tail", new OutboxMeta { Title = "x" });
        Assert.Equal(new string('x', 149) + ".pdf", name);
    }

    [Fact]
    public void Replaces_control_characters_and_trailing_dots()
    {
        Assert.Equal("a_b.pdf", DocumentFileName.Build("a\tb...", new OutboxMeta { Title = "x" }));
        Assert.Equal("a_b.pdf", DocumentFileName.Build("  a\u0001b...  ", new OutboxMeta { Title = "x" }));
    }

    [Fact]
    public void Uses_the_document_date()
    {
        var time = new TemplateFixedTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), TimeSpan.Zero);
        var meta = new OutboxMeta { Title = "x", Printed = DateTimeOffset.Parse("2026-01-02T03:04:05Z") };
        Assert.Equal("2026-01-02 030405.pdf", DocumentFileName.Build("{datetime}", meta, time));
    }
}
