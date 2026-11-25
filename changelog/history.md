## 2026-11-25 (41b7797)

- New: Mods can recommend other mods. The install dialog offers them in a box that is off by default.
- New: Mod details list recommended mods and show which of them are already installed.
- New: Mod details show what's new in the version you would update to.
- New: A Version history button opens every version's changes in its own scrollable window.
- Improved: Update dialogs show what changed for each mod being updated, and change notes support headings, bold, italics and code.
- Fixed: Browse cards no longer show an "Installed" label on top of the Update button.

## 2026-11-24 (ebf3cc3)

- Improved: The install and update dialog lists one row per mod, with the old and new version.
- Improved: The technical details of each mod are hidden behind a Details button, keeping the dialog short.
- Improved: Missing items are reported as "not offered by any source".

## 2026-11-22 (bbcaae1)

- New: Mods can require a minimum version of another mod, such as sacred-gear 0.4.1.
- Improved: Installing a mod first updates a required mod that is too old.
- Improved: If no source has a new enough version, nothing is installed and the dialog explains why.
- Improved: Switching on a mod stays off, with a message, while a required mod is too old.
- Improved: Mod details show each minimum version and whether your installed copy meets it.

## 2026-11-22 (3b66f9e)

- Improved: Mod packages with unsafe file paths are refused on every system.

## 2026-11-22 (c87fc28)

- Under the hood: game and mod folder paths are now handled by one shared rule.

## 2026-11-22 (d4a5144)

- New: Zip, tar, tar.gz and tar.zst mod archives install by drop, Ctrl+O or Open with.
- New: A plugin that shares a file with an installed plugin asks first: swap them, or cancel.
- New: Releases that wrap an archive inside another archive can be installed now.
- New: Small plugin files can be downloaded directly and checked before they are installed.
- Improved: The install question lists every file, where it goes, and where it comes from.
- Improved: Installs never run programs or scripts; such entries are refused or ignored.
- Improved: Downloads only follow redirects to secure https addresses.

## 2026-11-21 (03583f1)

- New: Sacred-Bild and the Gear mods are available from the first start, no source to add.
- Improved: The manager finds Sacred on its own when it sits next to Sacred.exe.
- Improved: A warning shows when a package holds another version than its listing promises.
- Fixed: Updated mod pictures now show up within an hour, and old ones stay when offline.
- Improved: The beginner guide has five clear steps and new screenshots.

## 2026-11-20 (0d1dee1)

- New: Sort the Installed page by load order to see the order Sacred Gear loads mods in.
- New: Move mods with arrow buttons, Alt+Up and Alt+Down, or by dragging; Ctrl+Z undoes it.
- New: Mods that Sacred Gear skips are marked "Won't load", with the reason.
- New: A banner shows when a newer version is out, linking to its page; switch it off in Settings.
- New: Plugin zips (Sacred-Bild, Sacred Gear) are recognised, and the manager offers their source.
- Improved: Dropped connections and busy servers are retried, and errors are explained in plain words.
- Improved: Only a Sacred running from your own game folder blocks changes, so a test copy no longer does.

## 2026-11-20 (d788313)

- Improved: The README is now a short get-started guide; the details moved to a Features page.
- New: The Sacred-Bild source brings Sacred-Bild, Sacred Gear and the Gear mods in one add.
- New: Web pages can offer install buttons; the manager asks before adding a source or installing a mod.

## 2026-11-19 (4be9809)

- Improved: Updates only overwrite files and never delete anything, even files the new version drops.
- Improved: Files an update leaves behind are still removed when you remove the mod.
- Improved: The "Files changed" notice explains that your edits stay in the backup and the mod's own data is untouched.
- Fixed: Tools on Linux start even when the index spells the program's name with different capitals.

## 2026-11-19 (ebd6e04)

- New: Browse and Installed have Mods, Plugins and Tools tabs with counts.
- New: Plugins copy files into your Sacred folder and restore the originals when removed.
- New: Tools get a Start button; their folder can be chosen in Settings.
- New: Mod lists can point to the newest GitHub release of a mod, plugin or tool.
- Improved: The install dialog lists plugins and tools and where their files go.
- Improved: The Linux download is an AppImage; absolute folder paths work as local sources there.
- Fixed: On Linux, links sent to a running manager no longer get lost.

## 2026-11-19 (f18e8c2)

- New: Installs, updates, switches on or off, and removes Sacred Gear mods from mod indexes.
- New: Browse mods as cards with pictures; installing one also installs what it requires.
- New: Undo changes, or restore the previous copy of a mod.
- New: Safe updates and removal keep your mod's own settings and saved data.
- New: Drop a mod zip onto the window to install mods that aren't in any index.
