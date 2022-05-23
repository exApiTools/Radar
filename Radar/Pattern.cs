using System;
using System.Globalization;
using System.Linq;
using ExileCore.Shared.Interfaces;

namespace Radar;

public class Pattern : IPattern
{
    public Pattern(string pattern, string name)
    {
        var arr = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var patternOffset = arr.FindIndex(x => x == "^");
        if (patternOffset == -1)
        {
            patternOffset = 0;
        }
        else
        {
            arr.RemoveAt(patternOffset);
        }

        PatternOffset = patternOffset;
        Bytes = arr.Select(x => x == "??" ? (byte)0 : byte.Parse(x, NumberStyles.HexNumber)).ToArray();
        Mask = arr.Select(x => x != "??").ToArray();
        Name = name;
        while (!Mask[0])
        {
            PatternOffset--;
            Mask = Mask.Skip(1).ToArray();
            Bytes = Bytes.Skip(1).ToArray();
        }

        while (!Mask[^1])
        {
            Mask = Mask.SkipLast(1).ToArray();
            Bytes = Bytes.SkipLast(1).ToArray();
        }
    }

    public string Name { get; }
    public byte[] Bytes { get; }
    public bool[] Mask { get; }
    public int StartOffset => 0;
    public int PatternOffset { get; }
    private string _mask;
    string IPattern.Mask => _mask ??= new string(Mask.Select(x => x ? 'x' : '?').ToArray());
}