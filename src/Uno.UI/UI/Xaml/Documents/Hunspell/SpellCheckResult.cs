#nullable enable
using System.Diagnostics;

namespace WeCantSpell.Hunspell;

[DebuggerDisplay("{Correct}: Info = {Info}, Root = {Root}")]
public readonly struct SpellCheckResult
{
	public static SpellCheckResult DefaultCorrect { get; } = Success(root: null, SpellCheckResultType.None);

	public static SpellCheckResult DefaultWrong { get; } = Fail(root: null, SpellCheckResultType.None);

	internal static SpellCheckResult Fail(string? root, SpellCheckResultType info) =>
		new(root, info: info, correct: false);

	internal static SpellCheckResult Success(string? root, SpellCheckResultType info) =>
		new(root, info: info, correct: true);

	internal static SpellCheckResult CompoundSuccess(string? root, SpellCheckResultType info) =>
		new(root, info: info | SpellCheckResultType.Compound, correct: true);

	public SpellCheckResult(string? root, SpellCheckResultType info, bool correct)
	{
		Root = root ?? string.Empty;
		Info = info;
		Correct = correct;
	}

	public string Root { get; }

	public SpellCheckResultType Info { get; }

	public bool Correct { get; }

	public bool Equals(SpellCheckResult other) => Info == other.Info && Correct == other.Correct && string.Equals(Root, other.Root, System.StringComparison.Ordinal);

	public override bool Equals(object? obj) => obj is SpellCheckResult other && Equals(other);

	public override int GetHashCode()
	{
		var hashCode = 175306637;
		hashCode = hashCode * -1521134295 + Correct.GetHashCode();
		hashCode = hashCode * -1521134295 + Info.GetHashCode();
		hashCode = hashCode * -1521134295 + (Root?.GetHashCode() ?? 0);
		return hashCode;
	}

	public static bool operator ==(SpellCheckResult left, SpellCheckResult right) => left.Equals(right);

	public static bool operator !=(SpellCheckResult left, SpellCheckResult right) => !left.Equals(right);
}
