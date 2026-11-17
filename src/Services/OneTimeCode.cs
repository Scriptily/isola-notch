using System.Text.RegularExpressions;

namespace Isola.Services;

/// <summary>
/// Finds a one-time code (a sign-in or verification code) in a notification,
/// the way Windows' own notifications offer to copy one. While the island
/// shows notifications, Windows' banners (and their Copy button) are off, so
/// the island offers it instead.
///
/// It looks for 4 to 8 digits (also "123 456", "123-456" and "G-123456") in a
/// message that talks about a code. It leaves alone what only looks like one:
/// amounts, phone numbers, order numbers, times, dates, years and percentages.
/// </summary>
public static partial class OneTimeCode
{
    // the message has to be about a code, or any number would do
    [GeneratedRegex(@"\b(code|codes|otp|passcode|pass code|password|verification|verify|one[- ]time|2fa|two[- ]factor|2-step|security|log ?in|sign[- ]?in|pin|authenticat\w*|confirm\w*|token)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Talk();

    // the code itself: digits, alone (not part of a longer number, a decimal, a
    // time, a date, or a spaced-out run of digits like a phone number)
    [GeneratedRegex(@"(?<![\w$€£¥#+.,:/\\-])(?<!\d[ -])(?:[A-Z]{1,3}-)?(?<code>\d{4,8}|\d{3}[ -]\d{3}|\d{4}[ -]\d{4})(?![\w%$€£,/:]|\.\d|-\d|[ -]\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Candidate();

    // words right before a number that make it something else
    [GeneratedRegex(@"(order|invoice|ticket|tracking|account|acct|ref(erence)?|case|room|flight|booking|po|no\.?|number|#|total|amount|balance|pay|paid|price|cost|usd|sar|aed|eur|gbp|rs|sr|ext|call|phone|tel|mobile)\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Before();

    // words right after a number that make it something else
    [GeneratedRegex(@"^\W*(usd|sar|aed|eur|gbp|dollars?|riyals?|points?|steps?|km|mi|kg|mb|gb|items?|followers?|likes?|views?|am|pm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex After();

    /// <summary>The code to copy (digits only), or null when there's none.</summary>
    public static string Find(string title, string body)
    {
        string text = $"{title} \n {body}";
        if (string.IsNullOrWhiteSpace(text) || !Talk().IsMatch(text)) return null;
        string best = null; int bestScore = int.MinValue;
        foreach (Match m in Candidate().Matches(text))
        {
            string digits = new string(m.Groups["code"].Value.Where(char.IsDigit).ToArray());
            string before = text[..m.Index], after = text[(m.Index + m.Length)..];
            if (Before().IsMatch(before.TrimEnd().Length > 24 ? before.TrimEnd()[^24..] : before.TrimEnd())) continue;
            if (After().IsMatch(after)) continue;
            // a year is a year unless it's plainly the code ("code: 2024")
            if (digits.Length == 4 && digits is ['1' or '2', '0' or '9', _, _] && !Regex.IsMatch(before, @"(code|otp|pin|passcode)(\s+(is|was))?\W{0,3}$", RegexOptions.IgnoreCase)) continue;
            // nearest to the talk of a code wins; six digits is the usual length
            int score = -Distance(text, m.Index) + (digits.Length == 6 ? 30 : 0) + (digits.Length is >= 5 and <= 8 ? 10 : 0);
            if (score > bestScore) { bestScore = score; best = digits; }
        }
        return best;
    }

    static int Distance(string text, int at)
    {
        int best = int.MaxValue;
        foreach (Match t in Talk().Matches(text)) best = Math.Min(best, Math.Abs(t.Index - at));
        return best == int.MaxValue ? 1000 : best;
    }

    /// <summary>"123 456" for six digits (easier to read back), the code as is otherwise.</summary>
    public static string Pretty(string code) => code is { Length: 6 } ? code[..3] + " " + code[3..] : code is { Length: 8 } ? code[..4] + " " + code[4..] : code;
}
