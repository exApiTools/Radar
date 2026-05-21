namespace Radar;

public record TargetDescription : TargetDescriptionAlternative
{
    public string DisplayName { get; set; }
    public TargetType TargetType { get; set; }
    public string Color { get; set; }
    public TargetDescriptionAlternative[] Alternatives { get; set; }

    internal string EqualityId => $"{Name}#{string.Join(",", Rooms ?? [])}";
}

public record TargetDescriptionAlternative
{
    public string Name { get; set; } = "";
    public string[] Rooms { get; set; }
    public int ExpectedCount { get; set; } = 1;
}