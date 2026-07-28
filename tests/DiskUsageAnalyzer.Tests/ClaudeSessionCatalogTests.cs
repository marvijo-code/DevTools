using DevTools.Services.Resources;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ClaudeSessionCatalogTests
{
    [Theory]
    [InlineData(@"C:\dev\DevTools", "C--dev-DevTools")]
    [InlineData(@"C:\dev\sample-web-app", "C--dev-sample-web-app")]
    [InlineData(
        @"C:\dev\sample-web-app\.claude\worktrees\happy-jemison-561c5c",
        "C--dev-sample-web-app--claude-worktrees-happy-jemison-561c5c")]
    public void EncodeProjectDirectory_MatchesTheSlugClaudeUsesForProjectFolders(string path, string expected)
    {
        Assert.Equal(expected, ClaudeSessionCatalog.EncodeProjectDirectory(path));
    }

    [Fact]
    public void EncodeProjectDirectory_IgnoresATrailingSeparator()
    {
        Assert.Equal(
            ClaudeSessionCatalog.EncodeProjectDirectory(@"C:\dev\DevTools"),
            ClaudeSessionCatalog.EncodeProjectDirectory(@"C:\dev\DevTools\"));
    }

    [Fact]
    public void ParseTitle_ReadsTheGeneratedTitle()
    {
        var transcript = string.Join('\n',
            """{"type":"user","message":{"role":"user","content":"hi"}}""",
            """{"type":"ai-title","aiTitle":"Analyze and fix daily system performance issues","sessionId":"abc"}""");

        Assert.Equal("Analyze and fix daily system performance issues", ClaudeSessionCatalog.ParseTitle(transcript));
    }

    [Fact]
    public void ParseTitle_PrefersAUserAssignedTitleOverTheGeneratedOne()
    {
        // The custom title appears first here, so ordering must not decide the winner.
        var transcript = string.Join('\n',
            """{"type":"custom-title","customTitle":"Daily system performance analysis","sessionId":"abc"}""",
            """{"type":"ai-title","aiTitle":"Analyze and fix daily system performance issues","sessionId":"abc"}""");

        Assert.Equal("Daily system performance analysis", ClaudeSessionCatalog.ParseTitle(transcript));
    }

    [Fact]
    public void ParseTitle_TakesTheMostRecentTitleOfTheSameKind()
    {
        var transcript = string.Join('\n',
            """{"type":"ai-title","aiTitle":"First guess","sessionId":"abc"}""",
            """{"type":"ai-title","aiTitle":"Better title once the work was clear","sessionId":"abc"}""");

        Assert.Equal("Better title once the work was clear", ClaudeSessionCatalog.ParseTitle(transcript));
    }

    [Fact]
    public void ParseTitle_SurvivesTheTruncatedFirstLineOfATailRead()
    {
        // Reading a fixed-size tail almost always starts mid-record; that fragment must not throw.
        var transcript = string.Join('\n',
            """ontent":"...truncated fragment of a much larger record"}""",
            """{"type":"ai-title","aiTitle":"Real title","sessionId":"abc"}""");

        Assert.Equal("Real title", ClaudeSessionCatalog.ParseTitle(transcript));
    }

    [Fact]
    public void ParseTitle_SurvivesATruncatedFinalLineOfALiveTranscript()
    {
        var transcript = string.Join('\n',
            """{"type":"ai-title","aiTitle":"Real title","sessionId":"abc"}""",
            """{"type":"assistant","message":{"role":"assist""");

        Assert.Equal("Real title", ClaudeSessionCatalog.ParseTitle(transcript));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"no title anywhere"}}""")]
    public void ParseTitle_ReturnsNullWhenNoTitleIsPresent(string transcript)
    {
        Assert.Null(ClaudeSessionCatalog.ParseTitle(transcript));
    }

    [Fact]
    public void ParseTitle_IgnoresBlankTitles()
    {
        var transcript = """{"type":"ai-title","aiTitle":"   ","sessionId":"abc"}""";

        Assert.Null(ClaudeSessionCatalog.ParseTitle(transcript));
    }

    [Fact]
    public void ParseFirstPrompt_ReadsTheQueuedOpeningPrompt()
    {
        var transcript = string.Join('\n',
            """{"type":"queue-operation","operation":"enqueue","content":"run the bench on Opus5","sessionId":"a"}""",
            """{"type":"queue-operation","operation":"dequeue","sessionId":"a"}""");

        Assert.Equal("run the bench on Opus5", ClaudeSessionCatalog.ParseFirstPrompt(transcript));
    }

    [Fact]
    public void ParseFirstPrompt_FallsBackToTheFirstUserMessage()
    {
        var transcript = """{"type":"user","message":{"role":"user","content":"check the fixtures cache"}}""";

        Assert.Equal("check the fixtures cache", ClaudeSessionCatalog.ParseFirstPrompt(transcript));
    }

    [Fact]
    public void ParseFirstPrompt_SkipsMultimodalTurnsThatHaveNoPlainText()
    {
        // Content is an array of blocks here, which carries no single line worth showing as a title.
        var transcript = string.Join('\n',
            """{"type":"user","message":{"role":"user","content":[{"type":"image"}]}}""",
            """{"type":"user","message":{"role":"user","content":"the actual question"}}""");

        Assert.Equal("the actual question", ClaudeSessionCatalog.ParseFirstPrompt(transcript));
    }

    [Fact]
    public void ParseFirstPrompt_CollapsesWhitespaceAndElidesLongPrompts()
    {
        var longPrompt = "make sure we have a tab for monitoring Resources.\n\nit must show the pc and usage over time, something is hurting my pc";
        var transcript = $$"""{"type":"queue-operation","operation":"enqueue","content":"{{longPrompt.Replace("\n", "\\n")}}","sessionId":"a"}""";

        var parsed = ClaudeSessionCatalog.ParseFirstPrompt(transcript);

        Assert.NotNull(parsed);
        Assert.DoesNotContain('\n', parsed!);
        Assert.True(parsed.Length <= 80, $"Expected an elided title, got {parsed.Length} characters.");
        Assert.EndsWith("…", parsed, StringComparison.Ordinal);
        Assert.StartsWith("make sure we have a tab for monitoring Resources.", parsed, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFirstPrompt_ReturnsNullWhenThereIsNoPrompt()
    {
        Assert.Null(ClaudeSessionCatalog.ParseFirstPrompt(""));
        Assert.Null(ClaudeSessionCatalog.ParseFirstPrompt("""{"type":"assistant","message":{"role":"assistant"}}"""));
    }

    [Fact]
    public void ResolveTitle_UsesTheOpeningPromptWhenTheSessionHasNoGeneratedTitle()
    {
        // Live sessions frequently have no title record at all, which is the common case in practice.
        var claudeHome = Path.Combine(Path.GetTempPath(), $"devtools-tests-{Guid.NewGuid():N}");
        var sessionId = "b5320e27-c641-4e5a-81a6-f9641bf7bc91";
        var projectDirectory = Path.Combine(
            claudeHome,
            "projects",
            ClaudeSessionCatalog.EncodeProjectDirectory(@"C:\dev\DevTools"));

        Directory.CreateDirectory(Path.Combine(claudeHome, "sessions"));
        Directory.CreateDirectory(projectDirectory);

        try
        {
            File.WriteAllText(
                Path.Combine(claudeHome, "sessions", "7000.json"),
                $$"""{"pid":7000,"sessionId":"{{sessionId}}","cwd":"C:\\dev\\DevTools","name":"devtools-99"}""");

            File.WriteAllText(
                Path.Combine(projectDirectory, $"{sessionId}.jsonl"),
                string.Join('\n',
                    """{"type":"queue-operation","operation":"enqueue","content":"add a resource monitoring tab","sessionId":"x"}""",
                    """{"type":"assistant","message":{"role":"assistant"}}"""));

            Assert.Equal(
                "add a resource monitoring tab",
                new ClaudeSessionCatalog(claudeHome).ResolveTitle([7000]));
        }
        finally
        {
            Directory.Delete(claudeHome, recursive: true);
        }
    }

    [Fact]
    public void TryGetSession_ReturnsNullWhenThereIsNoSessionFileForThePid()
    {
        var catalog = new ClaudeSessionCatalog(Path.Combine(Path.GetTempPath(), $"devtools-tests-{Guid.NewGuid():N}"));

        Assert.Null(catalog.TryGetSession(4242));
        Assert.Null(catalog.TryGetSession(0));
        Assert.Null(catalog.TryGetSession(-1));
    }

    [Fact]
    public void TryGetSession_ReadsThePidToSessionMappingTheCliWrites()
    {
        var claudeHome = Path.Combine(Path.GetTempPath(), $"devtools-tests-{Guid.NewGuid():N}");
        var sessionsDirectory = Path.Combine(claudeHome, "sessions");
        Directory.CreateDirectory(sessionsDirectory);

        try
        {
            File.WriteAllText(
                Path.Combine(sessionsDirectory, "132324.json"),
                """
                {"pid":132324,"sessionId":"e677cd41-64b7-4716-83e9-bb461d5ee8ad",
                 "cwd":"C:\\dev\\DevTools","name":"happy-jemison-561c5c-85"}
                """);

            var session = new ClaudeSessionCatalog(claudeHome).TryGetSession(132324);

            Assert.NotNull(session);
            Assert.Equal("e677cd41-64b7-4716-83e9-bb461d5ee8ad", session!.SessionId);
            Assert.Equal(@"C:\dev\DevTools", session.WorkingDirectory);
            Assert.Equal("happy-jemison-561c5c-85", session.Name);
        }
        finally
        {
            Directory.Delete(claudeHome, recursive: true);
        }
    }

    [Fact]
    public void ResolveTitle_FindsTheTitleThroughWhicheverProcessOwnsTheSession()
    {
        var claudeHome = Path.Combine(Path.GetTempPath(), $"devtools-tests-{Guid.NewGuid():N}");
        var workingDirectory = @"C:\dev\DevTools";
        var sessionId = "49d6c166-dfb6-44a6-970c-88819cf68cc6";
        var projectDirectory = Path.Combine(
            claudeHome,
            "projects",
            ClaudeSessionCatalog.EncodeProjectDirectory(workingDirectory));

        Directory.CreateDirectory(Path.Combine(claudeHome, "sessions"));
        Directory.CreateDirectory(projectDirectory);

        try
        {
            File.WriteAllText(
                Path.Combine(claudeHome, "sessions", "5150.json"),
                $$"""{"pid":5150,"sessionId":"{{sessionId}}","cwd":"C:\\dev\\DevTools","name":"n"}""");

            File.WriteAllText(
                Path.Combine(projectDirectory, $"{sessionId}.jsonl"),
                string.Join('\n',
                    """{"type":"user","message":{"role":"user","content":"go"}}""",
                    $$"""{"type":"ai-title","aiTitle":"Daily system performance analysis","sessionId":"{{sessionId}}"}"""));

            var catalog = new ClaudeSessionCatalog(claudeHome);

            // The first PID owns no session; the resolver must fall through to the one that does.
            Assert.Equal("Daily system performance analysis", catalog.ResolveTitle([9999, 5150]));
        }
        finally
        {
            Directory.Delete(claudeHome, recursive: true);
        }
    }

    [Fact]
    public void ResolveTitle_ReturnsNullWhenNoProcessOwnsASession()
    {
        var catalog = new ClaudeSessionCatalog(Path.Combine(Path.GetTempPath(), $"devtools-tests-{Guid.NewGuid():N}"));

        Assert.Null(catalog.ResolveTitle([1, 2, 3]));
    }
}
