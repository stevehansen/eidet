using Eidet.Core.Intake.Git;

namespace Eidet.Core.Tests.Intake.Git;

/// <summary>
/// Pure-function coverage of <see cref="GitCliAdapter"/>'s log/diff parsers — the only part of
/// the subprocess adapter that is testable without spawning git.
/// </summary>
public class GitCliParserTests
{
    [Fact]
    public void ParseLog_ReadsFieldsNumstatAndMergeParents()
    {
        const string stdout =
            "\x1e" + "aaa111\x1f" + "p1\x1f" + "steve@example.com\x1f" + "2026-07-10T10:00:00+02:00\x1f" +
            "fix: null deref in scorer\x1f" + "Longer body\nover two lines\x1f" + "\n" +
            "3\t1\tsrc/RecallScorer.cs\n" +
            "-\t-\tassets/logo.png\n" +
            "1\t1\tsrc/{Old.cs => New.cs}\n" +
            "\x1e" + "bbb222\x1f" + "p1 p2\x1f" + "bot@example.com\x1f" + "2026-07-09T09:00:00+02:00\x1f" +
            "Merge pull request #61 from renovate/typescript\x1f" + "\x1f" + "\n";

        var commits = GitCliAdapter.ParseLog(stdout);

        Assert.Equal(2, commits.Count);

        var fix = commits[0];
        Assert.Equal("aaa111", fix.Sha);
        Assert.Equal("fix: null deref in scorer", fix.Subject);
        Assert.Equal("Longer body\nover two lines", fix.Body);
        Assert.Equal("steve@example.com", fix.AuthorEmail);
        Assert.False(fix.IsMerge);
        Assert.Equal(3, fix.Files.Count);
        Assert.Equal(new FileChange("src/RecallScorer.cs", 3, 1, ChangeKind.Modified), fix.Files[0]);
        Assert.Equal(new FileChange("assets/logo.png", 0, 0, ChangeKind.Modified), fix.Files[1]);
        Assert.Equal(ChangeKind.Renamed, fix.Files[2].Kind);

        var merge = commits[1];
        Assert.True(merge.IsMerge);
        Assert.Empty(merge.Files);
    }

    [Fact]
    public void ParseDiff_YieldsHunksWithHeadersAndPaths()
    {
        const string stdout =
            "diff --git a/src/A.cs b/src/A.cs\n" +
            "--- a/src/A.cs\n" +
            "+++ b/src/A.cs\n" +
            "@@ -12,1 +12,2 @@ public double Factor()\n" +
            "-old line\n" +
            "+new line\n" +
            "+second new line\n" +
            "diff --git a/src/Gone.cs b/src/Gone.cs\n" +
            "--- a/src/Gone.cs\n" +
            "+++ /dev/null\n" +
            "@@ -1,3 +0,0 @@\n" +
            "-deleted content\n";

        var hunks = GitCliAdapter.ParseDiff(stdout);

        Assert.Equal(2, hunks.Count);

        Assert.Equal("src/A.cs", hunks[0].Path);
        Assert.Equal("@@ -12,1 +12,2 @@ public double Factor()", hunks[0].Header);
        Assert.Equal(["-old line", "+new line", "+second new line"], hunks[0].Lines);

        Assert.Equal("src/Gone.cs", hunks[1].Path); // deleted file falls back to the a/ path
        Assert.Equal(["-deleted content"], hunks[1].Lines);
    }
}
