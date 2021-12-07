using System.Collections.Generic;
using System.Text.RegularExpressions;
using GameOffsets.Native;
using SharpDX;

namespace Radar;

public static class Extensions
{
    public static SharpDX.Vector2 ToSdx(this System.Numerics.Vector2 v)
    {
        return new SharpDX.Vector2(v.X, v.Y);
    }

    public static Vector2i Truncate(this SharpDX.Vector2 v)
    {
        return new Vector2i((int)v.X, (int)v.Y);
    }

    public static SharpDX.Vector2 XY(this SharpDX.Vector3 v)
    {
        return new Vector2(v.X, v.Y);
    }

    public static IEnumerable<T> GetEveryNth<T>(this IEnumerable<T> source, int n)
    {
        var i = 0;
        foreach (var item in source)
        {
            if (i == 0)
            {
                yield return item;
            }

            i++;
            i %= n;
        }
    }

    /// <summary>
    /// Compares the string against a given pattern.
    /// </summary>
    /// <param name="str">The string.</param>
    /// <param name="pattern">The pattern to match, where "*" means any sequence of characters, and "?" means any single character.</param>
    /// <returns><c>true</c> if the string matches the given pattern; otherwise <c>false</c>.</returns>
    public static bool Like(this string str, string pattern)
    {
        return new Regex("^"
                       + Regex.Escape(pattern)
                            .Replace(@"\*", ".*")
                            .Replace(@"\?", ".")
                       + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline)
           .IsMatch(str);
    }
}
