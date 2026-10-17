using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Railway.Engine.Orchestration;

/// <summary>
/// Translates and matches watch-trigger globs (supporting <c>**</c>, <c>*</c>, <c>?</c> and character
/// classes) against repo-relative, POSIX-separated paths, case-sensitively.
/// </summary>
/// <remarks>
/// <para>
/// Compiled patterns are cached by the raw glob. A pattern that fails to compile is cached as
/// <see langword="null" /> and reported as <see cref="GlobOutcome.Malformed" /> so callers can
/// diagnose it once. A match that exceeds <see cref="GlobRegexTimeout" /> is reported as
/// <see cref="GlobOutcome.TimedOut" /> instead of blocking the watching thread.
/// </para>
/// <para>
/// Extracted from <see cref="HookWatcher"/> so the watch loop/orchestration, the glob translation and
/// the file-event source seam can each be read in isolation. <see cref="HookWatcher.MatchesGlob" />
/// and <see cref="HookWatcher.MatchesAnyGlob" /> delegate here to preserve the public API.
/// </para>
/// </remarks>
internal static class HookWatchGlobMatcher
{
    /// <summary>
    /// Upper bound on a single glob match. A pathological pattern degrades to "no match"
    /// instead of blocking the watching thread.
    /// </summary>
    private static readonly TimeSpan GlobRegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Compiled watch globs, keyed by the raw glob. A null value marks a pattern that could not be
    /// compiled (malformed) so it is only diagnosed once.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex?> GlobRegexCache = new(StringComparer.Ordinal);

    /// <summary>Outcome of evaluating one watch glob against one path.</summary>
    internal enum GlobOutcome
    {
        /// <summary>The glob matched.</summary>
        Match,

        /// <summary>The glob is valid but did not match.</summary>
        NoMatch,

        /// <summary>The glob could not be compiled and is skipped.</summary>
        Malformed,

        /// <summary>The match exceeded the timeout and is skipped.</summary>
        TimedOut,
    }

    /// <summary>
    /// Determines whether <paramref name="pattern"/> (supporting <c>**</c>, <c>*</c>, <c>?</c> and
    /// character classes) matches the repo-relative, POSIX-separated <paramref name="path"/>
    /// (case-sensitive).
    /// </summary>
    /// <param name="pattern">Watch glob.</param>
    /// <param name="path">Repo-relative path using <c>/</c> separators.</param>
    /// <returns><see langword="true" /> when <paramref name="pattern"/> matches <paramref name="path"/>; otherwise, <see langword="false" />.</returns>
    public static bool MatchesGlob(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);

        return EvaluateGlob(pattern, path) == GlobOutcome.Match;
    }

    /// <summary>Determines whether any glob in <paramref name="globs"/> matches <paramref name="path"/>.</summary>
    /// <param name="globs">Watch globs.</param>
    /// <param name="path">Repo-relative path using <c>/</c> separators.</param>
    /// <returns><see langword="true" /> when any glob in <paramref name="globs"/> matches <paramref name="path"/>; otherwise, <see langword="false" />.</returns>
    public static bool MatchesAnyGlob(IEnumerable<string> globs, string path)
    {
        ArgumentNullException.ThrowIfNull(globs);
        ArgumentNullException.ThrowIfNull(path);

        foreach (var glob in globs)
        {
            if (MatchesGlob(glob, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluates a single glob. Malformed patterns and patterns that exceed the timeout
    /// degrade to no match instead of throwing or never firing.
    /// </summary>
    internal static GlobOutcome EvaluateGlob(string pattern, string path)
    {
        var regex = GlobRegexCache.GetOrAdd(pattern, static glob => CompileGlob(glob));

        if (regex is null)
        {
            return GlobOutcome.Malformed;
        }

        try
        {
            return regex.IsMatch(path) ? GlobOutcome.Match : GlobOutcome.NoMatch;
        }
        catch (RegexMatchTimeoutException)
        {
            return GlobOutcome.TimedOut;
        }
    }

    /// <summary>Compiles a glob to an anchored regex, or null when the pattern is malformed.</summary>
    private static Regex? CompileGlob(string glob)
    {
        try
        {
            return new Regex(
                "^" + GlobToRegex(glob) + "$",
                RegexOptions.CultureInvariant,
                GlobRegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Converts a glob to a regex pattern (without anchors).</summary>
    private static string GlobToRegex(string glob)
    {
        var builder = new StringBuilder(glob.Length * 2);
        var index = 0;

        while (index < glob.Length)
        {
            var current = glob[index];

            switch (current)
            {
                case '*':
                    if (index + 1 < glob.Length && glob[index + 1] == '*')
                    {
                        index += 2;

                        // `**/` matches zero or more leading path segments; a bare `**` matches anything.
                        if (index < glob.Length && glob[index] == '/')
                        {
                            index++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        index++;
                        builder.Append("[^/]*");
                    }

                    break;

                case '?':
                    index++;
                    builder.Append("[^/]");
                    break;

                case '[':
                    index = AppendCharacterClass(glob, index, builder);
                    break;

                default:
                    index++;
                    builder.Append(Regex.Escape(current.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }

    private static int AppendCharacterClass(string glob, int start, StringBuilder builder)
    {
        var index = start + 1;
        var negate = false;

        if (index < glob.Length && glob[index] is '!' or '^')
        {
            negate = true;
            index++;
        }

        var content = new StringBuilder();
        var closed = false;

        while (index < glob.Length)
        {
            var current = glob[index];

            if (current == ']' && content.Length > 0)
            {
                closed = true;
                index++;
                break;
            }

            content.Append(current);
            index++;
        }

        if (!closed)
        {
            // Unterminated class: treat the '[' as a literal.
            builder.Append("\\[");
            return start + 1;
        }

        var sanitized = SanitizeCharacterClass(content.ToString());

        if (sanitized is null)
        {
            // Empty class (e.g. `[]`): the '[' is a literal.
            builder.Append("\\[");
            return start + 1;
        }

        builder.Append('[');

        if (negate)
        {
            builder.Append('^');
        }

        builder.Append(sanitized);
        builder.Append(']');
        return index;
    }

    /// <summary>
    /// Renders the raw contents of a glob character class as a valid regex class body. Well-formed
    /// ranges (<c>a-z</c>) are preserved; a reversed (<c>z-a</c>), stray, or otherwise unusable class
    /// degrades to literal characters instead of producing an invalid regex.
    /// </summary>
    private static string? SanitizeCharacterClass(string content)
    {
        var builder = new StringBuilder(content.Length * 2);
        var index = 0;

        while (index < content.Length)
        {
            var current = content[index];

            if (current != '-'
                && index + 2 < content.Length
                && content[index + 1] == '-'
                && current <= content[index + 2])
            {
                builder.Append(EscapeClassChar(current));
                builder.Append('-');
                builder.Append(EscapeClassChar(content[index + 2]));
                index += 3;
                continue;
            }

            builder.Append(EscapeClassChar(current));
            index++;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>Escapes one character for safe inclusion inside a regex character class.</summary>
    private static string EscapeClassChar(char value) => value switch
    {
        '\\' => "\\\\",
        ']' => "\\]",
        '^' => "\\^",
        '[' => "\\[",
        '-' => "\\-",
        _ => Regex.Escape(value.ToString()),
    };
}