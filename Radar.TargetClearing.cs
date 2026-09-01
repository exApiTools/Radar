using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using Vector2 = System.Numerics.Vector2;

namespace Radar;

/// <summary>
/// Retires targets whose reason to exist has died.
///
/// A tile target is static: unlike an entity target, nothing about the terrain changes when the
/// thing it points at is dealt with, so "Arena" keeps sitting on the map for the rest of the run.
/// This watches for the corpse a <see cref="DeathCondition"/> describes, or the opened chest an
/// <see cref="OpenCondition"/> describes, and drops the target -- its map label, its legend row
/// and its route -- the first time one is seen.
///
/// The flag latches, because the corpse or the opened chest is only observable while the player is
/// close enough for the client to have the entity loaded; polling "is it dead right now" would put
/// the waypoint back the moment they walked away.
/// </summary>
public partial class Radar
{
    private sealed record ClearCondition(string EqualityId, Regex[] Paths, Room[] Rooms, MonsterRarity MinRarity);

    private sealed record ChestCondition(string EqualityId, Regex[] Paths, Room[] Rooms);

    private List<ClearCondition> _clearConditions = [];
    private List<ChestCondition> _chestConditions = [];

    // Written from Tick, read from the pathfinding tasks' completion callbacks.
    private readonly ConcurrentDictionary<string, bool> _clearedTargets = new();

    // Retired one location at a time, keyed by "<EqualityId>|<x>,<y>". A chest condition with a room
    // clause hides the encounter that was looted without touching an identical one across the map.
    private readonly ConcurrentDictionary<string, bool> _clearedLocations = new();

    public override Job Tick()
    {
        UpdateClearedTargets();
        UpdateOpenedTargets();
        return null;
    }

    /// <summary>
    /// Resolves each area target's <see cref="DeathCondition"/> against this instance once, so the
    /// per-tick check is a bounds test rather than a regex sweep over every room name.
    /// </summary>
    private void BuildClearConditions()
    {
        _clearedTargets.Clear();
        _clearedLocations.Clear();

        // Only a target that actually placed a waypoint here can be retired, and a description in
        // the "*" group belongs to every area -- filtering on that first keeps the room diagnostics
        // below about the maps where the target really lives.
        var presentTargets = _targetDescriptionsInArea.Values
            .Where(x => _clusteredTargetLocations.ContainsKey(x.EqualityId))
            .ToList();

        _clearConditions = presentTargets
            .Where(x => x.HideWhenDead != null)
            .Select(x =>
            {
                var condition = x.HideWhenDead;
                return new ClearCondition(
                    x.EqualityId,
                    (condition.Paths ?? []).Select(p => p.ToLikeRegex()).ToArray(),
                    ResolveRooms(condition.Rooms, x, nameof(TargetDescription.HideWhenDead)),
                    condition.Rarity);
            })
            .ToList();

        _chestConditions = presentTargets
            .Where(x => x.HideWhenOpened != null)
            .Select(x =>
            {
                var condition = x.HideWhenOpened;
                return new ChestCondition(
                    x.EqualityId,
                    (condition.Paths ?? []).Select(p => p.ToLikeRegex()).ToArray(),
                    ResolveRooms(condition.Rooms, x, nameof(TargetDescription.HideWhenOpened)));
            })
            .ToList();

        foreach (var condition in _clearConditions)
        {
            LogMessage($"Radar: '{condition.EqualityId}' hides once a {condition.MinRarity} monster dies" +
                       $" in {(condition.Rooms == null ? "this area" : condition.Rooms.Length + " matched room(s)")}", 5);
        }

        foreach (var condition in _chestConditions)
        {
            LogMessage($"Radar: '{condition.EqualityId}' hides once a chest is opened" +
                       $" in {(condition.Rooms == null ? "this area" : condition.Rooms.Length + " matched room(s)")}", 5);
        }
    }

    /// <summary>
    /// Resolves a clause's room name patterns against this instance. Null means "no room clause";
    /// an empty array means "a room clause that matched nothing here", which must never fire.
    /// </summary>
    private Room[] ResolveRooms(string[] patterns, TargetDescription target, string clauseName)
    {
        var roomPatterns = (patterns ?? []).Select(r => r.ToLikeRegex()).ToList();
        if (roomPatterns.Count == 0)
        {
            return null;
        }

        var rooms = _rooms.Where(r => roomPatterns.Any(p => p.IsMatch(r.Key))).SelectMany(r => r.Value).ToArray();
        if (rooms.Length == 0)
        {
            // Fails closed, so say so rather than leaving a waypoint that quietly never retires.
            LogError($"Radar: {clauseName} for '{target.DisplayName ?? target.Name}' names rooms that do not " +
                     $"exist in this area, so it will never hide. Rooms: {string.Join(", ", patterns)}");
        }

        return rooms;
    }

    private void UpdateClearedTargets()
    {
        if (_clearConditions.Count == 0 || _clearedTargets.Count == _clearConditions.Count) return;

        var monsters = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster];
        if (monsters == null) return;

        foreach (var monster in monsters)
        {
            if (monster is not { IsValid: true } || monster.IsAlive) continue;

            foreach (var condition in _clearConditions)
            {
                if (_clearedTargets.ContainsKey(condition.EqualityId)) continue;
                if (!Matches(condition, monster)) continue;

                _clearedTargets[condition.EqualityId] = true;
                HideTarget(condition.EqualityId);
            }
        }
    }

    private static bool Matches(ClearCondition condition, Entity monster)
    {
        if (monster.Rarity < condition.MinRarity) return false;

        if (condition.Paths is { Length: > 0 })
        {
            var path = monster.Path;
            if (path == null) return false;
            if (!condition.Paths.Any(x => x.IsMatch(path))) return false;
        }

        if (condition.Rooms != null)
        {
            var pos = monster.GridPosNum;
            if (!condition.Rooms.Any(r => pos.X >= r.MinX && pos.X <= r.MaxX && pos.Y >= r.MinY && pos.Y <= r.MaxY))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A chest is only in the entity list while the player is close enough for the client to have
    /// loaded it -- which is exactly when they open it -- so seeing the flag once has to be enough.
    /// </summary>
    private void UpdateOpenedTargets()
    {
        if (_chestConditions.Count == 0) return;

        var chests = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Chest];
        if (chests == null) return;

        foreach (var chest in chests)
        {
            if (chest is not { IsValid: true }) continue;
            if (chest.GetComponent<Chest>() is not { IsOpened: true }) continue;

            foreach (var condition in _chestConditions)
            {
                if (!Matches(condition, chest, out var room)) continue;

                HideTargetLocations(condition.EqualityId, room);
            }
        }
    }

    private static bool Matches(ChestCondition condition, Entity chest, out Room room)
    {
        room = null;

        if (condition.Paths is { Length: > 0 })
        {
            var path = chest.Path;
            if (path == null) return false;
            if (!condition.Paths.Any(x => x.IsMatch(path))) return false;
        }

        if (condition.Rooms == null) return true;

        var pos = chest.GridPosNum;
        room = condition.Rooms.FirstOrDefault(r => pos.X >= r.MinX && pos.X <= r.MaxX && pos.Y >= r.MinY && pos.Y <= r.MaxY);
        return room != null;
    }

    /// <summary>
    /// Retires the waypoints that belong to <paramref name="room"/>, leaving the rest of the target
    /// alone -- two encounters of the same kind in one map are one description with two locations,
    /// and looting one of them says nothing about the other. A condition without a room clause has
    /// nothing to narrow by, so it retires the whole target.
    /// </summary>
    private void HideTargetLocations(string equalityId, Room room)
    {
        if (room == null)
        {
            if (!_clearedTargets.TryAdd(equalityId, true)) return;
            HideTarget(equalityId);
            return;
        }

        if (!_clusteredTargetLocations.TryGetValue(equalityId, out var existing)) return;

        var doomed = existing.Locations
            .Where(l => l.X >= room.MinX && l.X <= room.MaxX && l.Y >= room.MinY && l.Y <= room.MaxY)
            .ToArray();
        if (doomed.Length == 0)
        {
            // A waypoint is the room centre nudged onto walkable ground, and a cluster covering more
            // rooms than it has locations sits between them, so neither is guaranteed to land inside
            // the box. Falling back to the nearest one keeps the looted encounter from lingering.
            var centre = new Vector2((room.MinX + room.MaxX) / 2f, (room.MinY + room.MaxY) / 2f);
            doomed = existing.Locations.OrderBy(l => Vector2.DistanceSquared(l, centre)).Take(1).ToArray();
            if (doomed.Length == 0) return;
        }

        foreach (var location in doomed)
        {
            _clearedLocations[LocationKey(equalityId, location)] = true;
        }

        var survivors = existing.Locations.Except(doomed).ToArray();
        if (survivors.Length == 0)
        {
            _clusteredTargetLocations.TryRemove(equalityId, out _);
        }
        else
        {
            _clusteredTargetLocations[equalityId] = new TargetLocations { Locations = survivors, Target = existing.Target };
        }

        RemoveOrphanedRoutes(doomed);
    }

    private static string LocationKey(string equalityId, Vector2 location) =>
        $"{equalityId}|{location.X},{location.Y}";

    /// <summary>
    /// True once this target, or this one waypoint of it, has been retired. Pathfinding is
    /// asynchronous, so a route can still land after the thing it leads to was dealt with.
    /// </summary>
    private bool IsRetired(string equalityId, Vector2 location) =>
        _clearedTargets.ContainsKey(equalityId) ||
        _clearedLocations.ContainsKey(LocationKey(equalityId, location));

    private void HideTarget(string equalityId)
    {
        if (!_clusteredTargetLocations.TryRemove(equalityId, out var removed)) return;

        RemoveOrphanedRoutes(removed.Locations);
    }

    private void RemoveOrphanedRoutes(IEnumerable<Vector2> locations)
    {
        foreach (var location in locations)
        {
            // Two targets can cluster onto the same tile; the route belongs to the location, not
            // to the description, so it only goes when nothing else still points there.
            if (_clusteredTargetLocations.Any(x => Array.IndexOf(x.Value.Locations, location) >= 0)) continue;
            _routes.TryRemove(location, out _);
        }
    }
}
