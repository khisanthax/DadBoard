using System;
using DadBoard.Spine.Shared;

namespace DadBoard.Leader;

public sealed class LeaderInventoryCacheFile
{
    public string Ts { get; set; } = "";
    public SteamGameEntry[] Games { get; set; } = Array.Empty<SteamGameEntry>();
}

public sealed class AgentInventoriesCacheFile
{
    public GameInventory[] Inventories { get; set; } = Array.Empty<GameInventory>();
}
