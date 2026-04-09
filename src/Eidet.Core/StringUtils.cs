namespace Eidet.Core;

public static class StringUtils
{
    public static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}
