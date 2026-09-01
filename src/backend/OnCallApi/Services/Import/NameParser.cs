using System.Globalization;
using System.Text.RegularExpressions;

namespace OnCallApi.Services.Import;

/// <summary>How much the parser trusts its own reading of a name.</summary>
public enum NameConfidence
{
    /// <summary>Do not store this without someone looking at it.</summary>
    Low,

    /// <summary>A reasonable reading, but one that assumed something.</summary>
    Medium,

    /// <summary>The input said which part was which.</summary>
    High,
}

/// <summary>
/// One name column, read into the parts a directory needs.
///
/// <see cref="DisplayName"/> holds the original text whenever the parse was not confident,
/// so nothing is lost when a caller declines to trust <see cref="FirstName"/> and
/// <see cref="LastName"/>.
/// </summary>
public sealed record ParsedName(
    string FirstName,
    string LastName,
    string? MiddleName,
    string? Credentials,
    string? Suffix,
    string DisplayName,
    NameConfidence Confidence,
    string? ReviewReason);

/// <summary>
/// Splits a single name column into first and last names.
///
/// Real staff exports write one "Name" column and expect the reader to work it out:
/// "Doe, John", "Dr. Jane Smith MD", "SMITH, JANE", "John van der Berg". The importer
/// previously demanded two separate columns and failed every row of such a file with
/// "firstName and lastName are required" -- a message about the data, for a formatting
/// problem.
///
/// The parser reports a CONFIDENCE rather than always producing an answer. Guessing wrong
/// about which word is the surname is worse than asking: it is the name a colleague
/// searches for at two in the morning, and a directory that has filed someone under their
/// middle name is a directory that cannot find them.
/// </summary>
public static class NameParser
{
    /// <summary>
    /// Post-nominal letters, stripped from the end and kept separately. Taken as a closed
    /// set on purpose -- anything not listed stays part of the name, because dropping a
    /// real name fragment is worse than keeping a credential in it.
    /// </summary>
    private static readonly HashSet<string> Credentials = new(StringComparer.OrdinalIgnoreCase)
    {
        "MD", "DO", "RN", "BSN", "NP", "PA-C", "PA", "PharmD", "PhD", "LCSW", "RT",
        "MSN", "APRN", "CRNA", "DNP", "FNP", "DDS", "DMD", "MPH", "MBBS", "EdD", "PsyD",
    };

    /// <summary>Honorifics, stripped from the front.</summary>
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dr", "Dr.", "Mr", "Mr.", "Ms", "Ms.", "Mrs", "Mrs.", "Prof", "Prof.", "Miss",
    };

    /// <summary>Generational suffixes, which belong to neither the first nor last name.</summary>
    private static readonly HashSet<string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jr", "Jr.", "Sr", "Sr.", "II", "III", "IV", "V",
    };

    /// <summary>
    /// Surname particles. Once one appears, it and everything after it is the last name:
    /// "John van der Berg" is a Berg no more than "John de la Cruz" is a Cruz, and filing
    /// either under the wrong half makes them unfindable.
    ///
    /// Mac/Mc/O' are deliberately absent -- they are joined to the name ("MacDonald"), not
    /// separate tokens, so they need no grouping rule. They matter only to casing.
    /// </summary>
    private static readonly HashSet<string> Particles = new(StringComparer.OrdinalIgnoreCase)
    {
        "van", "von", "der", "den", "de", "del", "della", "di", "da", "dos", "das",
        "du", "la", "le", "bin", "ibn", "al", "ter", "ten", "vander",
    };

    private static readonly Regex Separators = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Reads one name column. Never throws; an unusable value comes back Low.</summary>
    public static ParsedName Parse(string? raw)
    {
        var original = (raw ?? string.Empty).Trim();
        if (original.Length == 0)
        {
            return new ParsedName("", "", null, null, null, "", NameConfidence.Low,
                "The name column was empty.");
        }

        // Casing is decided from the ORIGINAL text: "MCDONALD" should become "McDonald",
        // while "McDonald" must be left exactly as it is. Re-casing a name someone already
        // wrote correctly is a way to get it wrong.
        var shouting = IsAllCaps(original);

        var working = original;

        // 1. Credentials come off FIRST, before the comma is looked at. "Jane Smith, MD"
        //    and "Smith, Jane" both contain one comma and mean opposite things; removing
        //    the credential first leaves only the comma that separates surname from
        //    forename. Reading them in the other order files Jane under "Jane Smith".
        working = StripCredentials(working, out var credentials);

        // 2. Honorifics.
        working = StripLeadingTitles(working);

        // 3. A comma now means "surname first", the strongest signal available.
        var commaIndex = working.IndexOf(',');
        if (commaIndex > 0)
        {
            var last = working[..commaIndex].Trim();
            var rest = working[(commaIndex + 1)..].Trim();

            var restTokens = Tokenize(rest);
            restTokens = StripSuffix(restTokens, out var commaSuffix);

            if (last.Length > 0 && restTokens.Count > 0)
            {
                var first = restTokens[0];
                var middle = restTokens.Count > 1
                    ? string.Join(" ", restTokens.Skip(1))
                    : null;

                return Build(first, last, middle, credentials, commaSuffix, original,
                    shouting, NameConfidence.High, null);
            }

            // "Smith," with nothing after it is a surname alone, not a parsed name.
            if (last.Length > 0)
            {
                return new ParsedName("", "", null, credentials, commaSuffix,
                    Recase(last, shouting, isSurname: true), NameConfidence.Low,
                    $"'{original}' has a surname but no forename.");
            }
        }

        var tokens = Tokenize(working);
        tokens = StripSuffix(tokens, out var suffix);

        if (tokens.Count == 0)
        {
            return new ParsedName("", "", null, credentials, suffix,
                Recase(original, shouting, isSurname: false), NameConfidence.Low,
                $"'{original}' has no name in it once titles and credentials are removed.");
        }

        if (tokens.Count == 1)
        {
            // One word is not a person's name as far as this parser is concerned. It may
            // well be a unit label ("3North"), which the caller is better placed to judge.
            return new ParsedName("", "", null, credentials, suffix,
                Recase(tokens[0], shouting, isSurname: false), NameConfidence.Low,
                $"'{original}' is a single word, so there is no way to tell a forename from a surname.");
        }

        // A particle marks where the surname starts, whatever the token count.
        var particleAt = tokens.FindIndex(1, t => Particles.Contains(t.TrimEnd('-')));
        if (particleAt > 0)
        {
            var first = tokens[0];
            var middle = particleAt > 1 ? string.Join(" ", tokens.Skip(1).Take(particleAt - 1)) : null;
            var last = string.Join(" ", tokens.Skip(particleAt));

            return Build(first, last, middle, credentials, suffix, original, shouting,
                NameConfidence.High, null);
        }

        if (tokens.Count == 2)
        {
            return Build(tokens[0], tokens[1], null, credentials, suffix, original,
                shouting, NameConfidence.High, null);
        }

        if (tokens.Count == 3)
        {
            // Forename, middle name, surname is the common reading -- but it is a guess,
            // and an unrecognised particle or a two-word surname would break it.
            return Build(tokens[0], tokens[2], tokens[1], credentials, suffix, original,
                shouting, NameConfidence.Medium,
                $"'{original}' was read as first, middle and last name.");
        }

        return Build(tokens[0], string.Join(" ", tokens.Skip(1)), null, credentials, suffix,
            original, shouting, NameConfidence.Low,
            $"'{original}' has {tokens.Count} parts and no comma, so which of them form the surname is a guess.");
    }

    // ── Steps ──

    private static string StripCredentials(string value, out string? credentials)
    {
        credentials = null;
        var found = new List<string>();

        var working = value.TrimEnd();
        while (true)
        {
            // A credential may be separated by a comma or by a space, and either may be
            // repeated: "Smith, Jane, MD, FACS".
            var cut = working.LastIndexOfAny([',', ' ']);
            if (cut <= 0) break;

            var tail = working[(cut + 1)..].Trim().TrimEnd('.');
            if (tail.Length == 0 || !Credentials.Contains(tail)) break;

            found.Insert(0, tail);
            working = working[..cut].TrimEnd().TrimEnd(',').TrimEnd();
        }

        if (found.Count > 0) credentials = string.Join(", ", found);
        return working;
    }

    private static string StripLeadingTitles(string value)
    {
        var working = value.TrimStart();
        while (true)
        {
            var space = working.IndexOf(' ');
            if (space <= 0) break;

            var head = working[..space];
            if (!Titles.Contains(head)) break;

            working = working[(space + 1)..].TrimStart();
        }

        return working;
    }

    private static List<string> StripSuffix(List<string> tokens, out string? suffix)
    {
        suffix = null;
        if (tokens.Count < 2) return tokens;

        var last = tokens[^1].TrimEnd(',');
        if (!Suffixes.Contains(last)) return tokens;

        suffix = last.TrimEnd('.');
        return tokens[..^1];
    }

    private static List<string> Tokenize(string value) =>
        Separators.Split(value.Trim())
            .Select(t => t.Trim().Trim(','))
            .Where(t => t.Length > 0)
            .ToList();

    private static ParsedName Build(
        string first, string last, string? middle, string? credentials, string? suffix,
        string original, bool shouting, NameConfidence confidence, string? reviewReason)
    {
        var firstCased = Recase(first, shouting, isSurname: false);
        var lastCased = Recase(last, shouting, isSurname: true);

        return new ParsedName(
            firstCased,
            lastCased,
            middle == null ? null : Recase(middle, shouting, isSurname: false),
            credentials,
            suffix,
            $"{firstCased} {lastCased}".Trim(),
            confidence,
            reviewReason);
    }

    // ── Casing ──

    private static bool IsAllCaps(string value)
    {
        var letters = value.Where(char.IsLetter).ToList();
        return letters.Count > 1 && letters.All(char.IsUpper);
    }

    /// <summary>
    /// Fixes the casing of a name that arrived shouted, and otherwise leaves it alone.
    ///
    /// "MCDONALD" becomes "McDonald" and "O'BRIEN" becomes "O'Brien", because an export
    /// that upper-cases everything has destroyed information the reader can restore. A
    /// name that was already mixed-case is never touched: whoever typed "van der Berg" or
    /// "McDonald" knew what they meant.
    /// </summary>
    /// <param name="isSurname">
    /// Whether a particle in FIRST position should stay lower-case. It should in a
    /// surname -- "VAN DER BERG" is "van der Berg" -- and must not in a forename, where
    /// "AL" is a name in its own right and lower-casing it renames the person.
    /// </param>
    private static string Recase(string value, bool shouting, bool isSurname)
    {
        if (!shouting || value.Length == 0) return value;

        var parts = value.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;

            // A particle stays lower-case: "van der Berg", not "Van Der Berg".
            if ((i > 0 || isSurname) && Particles.Contains(part))
            {
                parts[i] = part.ToLowerInvariant();
                continue;
            }

            parts[i] = RecaseWord(part);
        }

        return string.Join(" ", parts);
    }

    private static string RecaseWord(string word)
    {
        var lower = word.ToLowerInvariant();
        var titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);

        // Scottish and Irish prefixes carry a capital on the following letter, which
        // ToTitleCase does not know about: it renders "MCDONALD" as "Mcdonald".
        foreach (var prefix in (string[])["mac", "mc"])
        {
            if (lower.Length > prefix.Length + 1 && lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                return string.Concat(
                    char.ToUpperInvariant(titled[0]),
                    titled[1..prefix.Length],
                    char.ToUpperInvariant(titled[prefix.Length]),
                    titled[(prefix.Length + 1)..]);
            }
        }

        // "O'brien" -> "O'Brien", and likewise for a hyphenated surname.
        foreach (var mark in (char[])['\'', '-'])
        {
            var at = titled.IndexOf(mark);
            if (at > 0 && at < titled.Length - 1)
            {
                titled = string.Concat(
                    titled[..(at + 1)], char.ToUpperInvariant(titled[at + 1]), titled[(at + 2)..]);
            }
        }

        return titled;
    }
}
