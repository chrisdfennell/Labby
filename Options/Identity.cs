namespace Labby.Options;

/// <summary>Roles carried on the auth cookie.</summary>
public static class Roles
{
    /// <summary>A kid signed in through the chores portal with their PIN.</summary>
    public const string Kid = "kid";
}

public static class Policies
{
    /// <summary>Everything that is not the kids' chore portal.</summary>
    public const string Console = "console";
}

/// <summary>Claims Labby adds beyond the standard set.</summary>
public static class LabbyClaims
{
    /// <summary>The family_members row a signed-in kid belongs to.</summary>
    public const string MemberId = "labby:member_id";
}
