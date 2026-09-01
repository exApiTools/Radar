using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;

namespace Radar;

/// <summary>
/// Retires targets whose reason to exist has died.
///
/// A tile target is static: unlike an entity target, nothing about the terrain changes when the
/// thing it points at is dealt with, so "Arena" keeps sitting on the map for the rest of the run.
/// This watches for the corpse a <see cref="DeathCondition"/> describes and drops the target --
/// its map label, its legend row and its route -- the first time one is seen.
///
/// The flag latches, because the corpse is only observable while the player is close enough for
/// the client to have the entity loaded; polling "is it dead right now" would put the waypoint
/// back the moment they walked away.
/// </summary>
public partial class Radar
{
    private sealed record ClearCondition(string EqualityId, Regex[] Paths, Room[] Rooms, MonsterRarity MinRarity);

    private List<ClearCondition> _clearConditions = [];

    // Written from Tick, read from the pathfinding tasks' completion callbacks.
    private readonly ConcurrentDictionary<string, bool> _clearedTargets = new();

    public override Job Tick()
    {
        UpdateClearedTargets();
        return null;
    }

    /// <summary>
    /// Resolves each area target's <see cref="DeathCondition"/> against this instance once, so the
    /// per-tick check is a bounds test rather than a regex sweep over every room name.
    /// </summary>
    private void BuildClearConditions()
    {
        _clearedTargets.Clear();
        _clearConditions = _targetDescriptionsInArea.Values
            .Where(x => x.HideWhenDead != null)
            .Select(x =>
            {
                var condition = x.HideWhenDead;
                var roomPatterns = (condition.Rooms ?? []).Select(r => r.ToLikeRegex()).ToList();
                // null means "no room clause"; an empty array means "a room clause that matched
                // nothing here", which must never fire.
                var rooms = roomPatterns.Count == 0
                    ? null
                    : _rooms.Where(r => roomPatterns.Any(p => p.IsMatch(r.Key))).SelectMany(r => r.Value).ToArray();
                if (rooms is { Length: 0 })
                {
                    // Fails closed, so say so rather than leaving a waypoint that quietly never retires.
                    LogError($"Radar: HideWhenDead for '{x.DisplayName ?? x.Name}' names rooms that do not " +
                             $"exist in this area, so it will never hide. Rooms: {string.Join(", ", condition.Rooms)}");
                }

                return new ClearCondition(
                    x.EqualityId,
                    (condition.Paths ?? []).Select(p => p.ToLikeRegex()).ToArray(),
                    rooms,
                    condition.Rarity);
            })
            .ToList();

        foreach (var condition in _clearConditions)
        {
            LogMessage($"Radar: '{condition.EqualityId}' hides once a {condition.MinRarity} monster dies" +
                       $" in {(condition.Rooms == null ? "this area" : condition.Rooms.Length + " matched room(s)")}", 5);
        }
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

    private void HideTarget(string equalityId)
    {
        if (!_clusteredTargetLocations.TryRemove(equalityId, out var removed)) return;

        foreach (var location in removed.Locations)
        {
            // Two targets can cluster onto the same tile; the route belongs to the location, not
            // to the description, so it only goes when nothing else still points there.
            if (_clusteredTargetLocations.Any(x => Array.IndexOf(x.Value.Locations, location) >= 0)) continue;
            _routes.TryRemove(location, out _);
        }
    }
}
