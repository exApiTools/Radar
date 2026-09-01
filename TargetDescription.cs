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

    /// <summary>
    /// Stop showing this target once a matching chest has been seen opened. A chart encounter is
    /// spent the moment its chest is looted, but the room it sits in stays in the area graph, so
    /// nothing else would ever retire the waypoint.
    /// </summary>
    public OpenCondition HideWhenOpened { get; set; }

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

/// <summary>
/// Which opened chest retires a target. Every populated clause must hold, so an empty condition
/// means "any chest anywhere in the area", and adding clauses narrows it.
/// </summary>
public record OpenCondition
{
    /// <summary>Chest metadata path patterns ("*" and "?" wildcards). Null matches any path.</summary>
    public string[] Paths { get; set; }

    /// <summary>
    /// Area-graph room name patterns the chest has to sit inside. With a room clause only the
    /// locations inside the room holding the chest retire, so a map with two of the same encounter
    /// loses the line to the one that was looted and keeps the other. Null retires the whole
    /// target, the way <see cref="DeathCondition"/> does.
    /// </summary>
    public string[] Rooms { get; set; }
}

public record TargetDescriptionAlternative
{
    public string Name { get; set; } = "";
    public string[] Rooms { get; set; }
    public int ExpectedCount { get; set; } = 1;
}
