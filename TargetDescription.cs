using ExileCore.Shared.Enums;

namespace Radar;

public record TargetDescription : TargetDescriptionAlternative
{
    public string DisplayName { get; set; }
    public TargetType TargetType { get; set; }
    public string Color { get; set; }
    public TargetDescriptionAlternative[] Alternatives { get; set; }

    /// <summary>
    /// Stop showing this target once a monster matching the condition has been seen dead.
    /// Meant for a waypoint whose only purpose is to lead somewhere -- a boss arena entrance
    /// is noise on the map once the boss is down.
    /// </summary>
    public DeathCondition HideWhenDead { get; set; }

    internal string EqualityId => $"{Name}#{string.Join(",", Rooms ?? [])}";
}

/// <summary>
/// Which corpse retires a target. Every populated clause must hold, so an empty condition
/// means "any unique monster anywhere in the area", and adding clauses narrows it.
/// </summary>
public record DeathCondition
{
    /// <summary>Monster metadata path patterns ("*" and "?" wildcards). Null matches any path.</summary>
    public string[] Paths { get; set; }

    /// <summary>
    /// Area-graph room name patterns the monster has to die inside. Null matches the whole area.
    /// A pattern that matches no room in the current area never fires, so a target survives a
    /// layout that does not contain the room it was written for.
    /// </summary>
    public string[] Rooms { get; set; }

    /// <summary>
    /// Minimum rarity. Unique by default: a map boss qualifies, the adds it spawns do not.
    /// </summary>
    public MonsterRarity Rarity { get; set; } = MonsterRarity.Unique;
}

public record TargetDescriptionAlternative
{
    public string Name { get; set; } = "";
    public string[] Rooms { get; set; }
    public int ExpectedCount { get; set; } = 1;
}
