namespace Labby.Models;

/// <summary>A repository from MyPersonalGit's <c>/api/v1/repos</c>.</summary>
public sealed record GitRepo
{
    /// <summary>On-disk name, which the API returns with a ".git" suffix; used for per-repo API calls.</summary>
    public string RawName { get; init; } = "";
    /// <summary>Display name with the ".git" suffix stripped.</summary>
    public string Name => RawName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? RawName[..^4] : RawName;
    public string Description { get; init; } = "";
    public string Owner { get; init; } = "";
    public bool IsPrivate { get; init; }
    public int Stars { get; init; }
    public int Forks { get; init; }
    public int Commits { get; init; }
    public string DefaultBranch { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Open issue/PR counts, filled in by a follow-up per-repo fetch (null = not loaded).</summary>
    public int? OpenIssues { get; set; }
    public int? OpenPulls { get; set; }
}

public sealed record GitIssue
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public int CommentCount { get; init; }
    /// <summary>Repo the issue belongs to (display name), set by the aggregator.</summary>
    public string Repo { get; init; } = "";
}

public sealed record GitPull
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public bool IsDraft { get; init; }
    public string SourceBranch { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string Repo { get; init; } = "";
}

/// <summary>Everything the Git page and dashboard card render, with per-source errors.</summary>
public sealed record GitOverview
{
    public IReadOnlyList<GitRepo> Repos { get; init; } = [];
    public IReadOnlyList<GitIssue> OpenIssues { get; init; } = [];
    public IReadOnlyList<GitPull> OpenPulls { get; init; } = [];
    public string? Error { get; init; }

    public int RepoCount => Repos.Count;
    public int TotalStars => Repos.Sum(r => r.Stars);
    public GitRepo? MostRecent => Repos.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
}
