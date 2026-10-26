# Avalon Crafting Assistance

A Dalamud plugin for FINAL FANTASY XIV that lets the Avalon team work on [Teamcraft](https://ffxivteamcraft.com) lists in game:

- Pick your Teamcraft team and its lists from a drop-down; lists stay in sync with Teamcraft.
- Lists are divided like Teamcraft (final items, crystals, vendors, gathering, pre-crafts…).
- The team leader assigns items (or a whole list) to members; crafters can show only their own work.
- Assigned crafters mark progress (−, +, type an amount, complete, or fill from inventory), with ingredients updated the same way Teamcraft does.
- See what you own in bags, saddlebag and retainers.
- Cheapest North America market prices from [Universalis](https://universalis.app), and an estimate to buy the remaining materials.
- Leaders and officers send quotes to Avalon website tickets.
- Copy the remaining crafts as an [Artisan](https://github.com/PunishXIV/Artisan) list.

Access needs a personal key from the Avalon website (team crafters only).

## Install

1. In game, type `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, add
   `https://raw.githubusercontent.com/Kynyssael/AvalonCraftingAssistance/main/repo.json`,
   tick **Enabled**, and click **Save**.
3. Type `/xlplugins`, search for **Avalon Crafting Assistance** and install it.
4. Log in on [avalondesignsco.com](https://avalondesignsco.com), open **Plugin key** in your account menu and generate a key.
5. Type `/avalon config`, paste the key in **Avalon key**, then sign in to Teamcraft in the next tab.
6. Type `/avalon` to open the plugin.

## Releasing an update

1. Bump `<Version>` in `AvalonPlugin/AvalonPlugin.csproj`.
2. Run `powershell -File tools/release.ps1` (needs the .NET 10 SDK).
3. Commit `release/latest.zip` and `repo.json`, and push. Players get the update from `/xlplugins`.

## Notes

Teamcraft has no public API; the plugin talks to Teamcraft's database the same way Teamcraft's own site does,
so a Teamcraft update could break it. The website side of the key check is described in [docs/avalon-key-api.md](docs/avalon-key-api.md).
