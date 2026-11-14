namespace SimpleModManager;

internal sealed record ReleaseAsset(string Url, string Sha256);
internal sealed record Mod(string Id, string Name, string Author, string Category, string Version,
    string Description, string Repository, string FileName, string Sha256, string Color, string Artwork,
    string? Dependency = null, bool SteamOnly = false, bool Preview = false,
    UsageTag Usage = UsageTag.Unverified, string? DownloadUrl = null, string? Banner = null,
    string? PluginId = null, string? MinimumDependencyVersion = null,
    bool DependencyOnly = false, bool LocalFile = false, string? SourceUrl = null, string? Icon = null,
    string? BundledResource = null, string? ReleaseTag = null)
{
    public bool HasPackage => Sha256.Length > 0 &&
        (LocalFile || BundledResource is not null || DownloadUrl is not null || Repository.Length > 0);
    public bool Restricted => (Usage is UsageTag.Illegal or UsageTag.Restricted) && !HasPackage;
    public string? Source => Restricted ? null : SourceUrl ?? (LocalFile || BundledResource is not null ? null : DownloadUrl is null
        ? $"https://github.com/{Repository}/releases/tag/{ReleaseTag ?? Version}"
        : $"https://github.com/{Repository}");
    public ReleaseAsset Asset => Restricted || LocalFile || BundledResource is not null
        ? throw new InvalidOperationException("This catalog entry has no downloadable package.")
        : new(DownloadUrl ?? $"https://github.com/{Repository}/releases/download/{ReleaseTag ?? Version}/{FileName}", Sha256);
    public string DisplayVersion => Version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? Version : $"v{Version}";
}

internal enum UsageTag { Unverified, Verified, Legal, Illegal, Utility, Restricted }

internal static class Catalog
{
    public const string BepInExVersion = "5.4.23.6";
    public const string BepInExSource = "https://github.com/BepInEx/BepInEx/releases/tag/v" + BepInExVersion;
    public const string Discord = "https://discord.gg/itzgame-s-fries-1536853966741180476";
    public static readonly ReleaseAsset BepInEx = new(
        BepInExSource.Replace("/tag/", "/download/", StringComparison.Ordinal) + $"/BepInEx_win_x64_{BepInExVersion}.zip",
        "6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755");

    public static readonly Mod[] Mods =
    [
        new("mono-sandbox", "Mono Sandbox", "developer9998", "Gameplay", "1.2.4",
            "A Sandbox Mod for Gorilla Tag",
            "developer9998/MonoSandbox", "MonoSandbox.dll",
            "90c9e0bc48998bfeb169b3431bece045f9e7d7031ee50b2cb5e3698054fdce30", "#A7B8FF", "sandbox",
            Dependency: "gorilla-library", Usage: UsageTag.Legal, Banner: "mono-sandbox.png", MinimumDependencyVersion: "1.1.0"),
        new("bark", "Bark", "NgbatzYT", "Gameplay", "3.0.2",
            "An in-game menu of movement and sandbox tools for modded rooms.",
            "NgbatzYT/Bark", "Bark.dll",
            "345b1fcefdee85e94c9cbf046ff92edfd7008c568b533c5773fb2188d7769b22", "#F2C878", "bark",
            Dependency: "utilla", Usage: UsageTag.Legal, Banner: "bark.png", MinimumDependencyVersion: "1.7.2"),
        new("utilla", "Utilla", "legoandmars", "Libraries", "1.7.3",
            "Tools for joining modded rooms and for building room-aware PC mods.",
            "legoandmars/Utilla", "Utilla.dll",
            "4c64753e6f3609a40ca5d86e6033b134f915474ec7387d3fd517a95f255de813", "#E4CC8D", "nodes",
            Usage: UsageTag.Legal, DownloadUrl: "https://monkeforge.org/api/v1/downloads/a20eea00a5795e50fc4dd030de027f04",
            Banner: "utilla.png", PluginId: "org.legoandmars.gorillatag.utilla"),
        new("ii-reborn", "ii Reborn", "ii Menu", "Gameplay", "1.2.5",
            "Gorilla Tag Mod Menu with over 1900 Mods!",
            "iimenu/project", "ii.Reborn.dll",
            "26f74523cb4a677b78256ef6c96cc4cb9d8dd72499a34064736c38a0e551e2f9", "#FFA980", "cone",
            Usage: UsageTag.Illegal, Banner: "ii-reborn.png"),
        new("hamburbur", "Hamburbur", "hamburbur-org", "Gameplay", "V7.3.1",
            "Hamburbur, the most fun Gorilla Tag menu!",
            "hamburbur-org/hamburbur", "hamburbur.dll",
            "599a5591136336ebd2221d2145b1a12c6555ba60ea89a0ff7761a325ead4e792", "#FFE5B3", "hamburbur",
            SteamOnly: true, Usage: UsageTag.Illegal,
            DownloadUrl: "https://github.com/hamburbur-org/hamburbur/releases/latest/download/hamburbur.dll",
            Banner: "hamburbur.png", Icon: "hamburbur.png"),
        new("malachi-reborn", "Malachi's Menu Reborn", "Malachi", "Gameplay", "9.1.1",
            "Malachi's Menu Reborn, a fun and OP menu!",
            "Malachi-the-modder/ownerids", "Malachis_Menu_Reborn.dll",
            "4cfc36be1019a791ecaf26b2c0fcf2d063af7d3f4e7ef7fd8bbc050bfc6f5825", "#FFD164", "malachi",
            Usage: UsageTag.Illegal, Banner: "malachi-banner.png", Icon: "malachi-logo.jpg",
            ReleaseTag: "Malachis_Menu_Reborn"),
        new("gorilla-shirts", "Gorilla Shirts", "developer9998", "Cosmetics", "v2.4.9",
            "Wear community-made shirts and browse new packs at the in-game Shirt Stand.",
            "developer9998/GorillaShirts", "GorillaShirts.dll",
            "03bcdb034dcd7d67425419e15b930502f21ed9cdf2d672191855c9c2b03c2f4c", "#F6B49A", "shirt",
            Usage: UsageTag.Verified, Banner: "gorilla-shirts.png"),
        new("camera", "Pokruks Camera Mod", "Pokruk", "Utilities", "3.12.1",
            "Set up first-person, third-person, and spectator shots from an in-game tablet.",
            "Pokruk/CameraMod", "PokruksCameraMod.dll",
            "be36bd39a21c73d4273023d5bf674722f45308ffee96d8ff2ba20e8cb507d49c", "#CB9AFF", "lens",
            Usage: UsageTag.Legal, Banner: "pokruks-camera.png", LocalFile: true,
            SourceUrl: "https://github.com/Pokruk/CameraMod/releases"),
        new("update-changer", "Update Changer", "ItzGame", "Utilities", "1.1.0",
            "Switch the forest map between seasonal updates and their scenery.",
            "", "Update Changer.dll",
            "73323a81a4d44f94c26b36a9b88006b86c4cf739fceee8737c20342e56950936", "#C8CCD4", "swap",
            Usage: UsageTag.Legal, Banner: "update-changer-banner.png", Icon: "update-changer-icon.png",
            BundledResource: "SimpleModManager.Mods.UpdateChanger.dll"),
        new("i-hate-ghosts", "I Hate Ghosts", "SmoothesHub", "Utilities", "1.0.0",
            "Hides the Halloween Lurker and suppresses its sound and possession effects locally.",
            "SmoothesHub/I-Hate-Ghosts", "I.Hate.Ghosts.dll",
            "8fabae71c1d5ad61b2e527034f7f6260e4d107831f42d22ea9181376524ec71a", "#D6ABF9", "ghost",
            Usage: UsageTag.Utility, Banner: "i-hate-ghosts.png", Icon: "i-hate-ghosts.png", ReleaseTag: "I-Hate-Ghosts-V1"),
        new("gorilla-library", "GorillaLibrary", "GorillaTagModdingHub", "Libraries", "1.1.0",
            "Shared tools for PC mods, including custom game modes and developer utilities.",
            "GorillaTagModdingHub/GorillaLibrary", "GorillaLibrary.dll",
            "6c4ee9b89a0d96b92f750467da1334b6c257e21d0fc7a99820f97d5fb6907e1d", "#96DCC6", "cube",
            Usage: UsageTag.Utility, PluginId: "dev.gorillalibrary", DependencyOnly: true),
        new("phone", "GorillaPhone", "itsreallyhex", "Capture", "v0.1.0-pre.1",
            "Pick up an in-game phone, take photos and selfies, and browse your gallery.",
            "itsreallyhex/GorillaPhone", "GorillaPhone.dll",
            "1b9890bbf6120083221c60660664412d2e09297171b09e020cb33ca5273c3e04", "#F1B69A", "phone",
            Preview: true, Usage: UsageTag.Legal, Banner: "gorilla-phone.gif")
    ];
}
