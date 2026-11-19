# Mod index

A `mod-index.json` lists the mods of a release for desktop mod managers (the Gear Mod Manager reads it). The index of the
Sacred Gear mods is the reference example. It is plain JSON, UTF-8 without BOM. The release workflow publishes it as a release asset next to
`sacred-gear-mods.zip`, so the stable URLs are:

- `https://github.com/kirides/sacred-gear-mods/releases/latest/download/mod-index.json`
- `https://github.com/kirides/sacred-gear-mods/releases/latest/download/sacred-gear-mods.zip`

The zip holds `mods/<name>/...` (one folder per mod, ready to extract into the game folder) and `README.txt`.

## Index (schema 1)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `schema` | number | yes | Format version, `1` |
| `name` | string | yes | Name of the index / publisher |
| `author` | string | yes | Author of the index |
| `description` | string | no | One or two sentences |
| `homepage` | string | no | https URL of the project |
| `download` | object | no | The zip the `mods` come from: `url` (https, or relative to the index), `version` (the latest tag, or `latest`) and optionally `sha256` (hex of the zip; the manager rejects a download that does not match) |
| `mods` | array | no | The mods, see below |
| `indexes` | array | no | Other indexes to follow, see below |

An index lists its own `mods` and may also reference other indexes in `indexes`; both can be present in one file. An
index without `mods` is a pure meta-index: only `schema`, `name`, `author`, `description` and `indexes`. `download` is
needed when `mods` is present, unless every mod carries its own `download`.

## Mod entry

| Field | Type | Required | Meaning |
|---|---|---|---|
| `id` | string | yes | The `id` of the mod's `mod.lua` |
| `path` | string | yes | The mod's folder inside the zip, relative to the zip root: `mods/<folder>` |
| `name` | string | yes | Display name, copied from `mod.lua` |
| `version` | string | yes | Copied from `mod.lua` |
| `author` | string | no | Copied from `mod.lua` |
| `description` | string | no | Copied from `mod.lua` |
| `tags` | string[] | no | Short lowercase categories (`ui`, `quests`, `loot`, `companions`, `classes`, `multiplayer`, `tools`, `chat`, `saves`, ...) |
| `thumbnail` | string | no | An image: a path relative to the zip root, or an absolute https URL |
| `images` | string[] | no | Pictures for the viewer: clicking the mod's picture opens a carousel (arrow keys, dots, Esc). Each is a path relative to the zip root or an absolute https URL; at most 24. Without a `thumbnail` the first image is the list picture; without `images` the thumbnail is the only picture |
| `requires` | string[] | no | Ids of mods this one needs, copied from `mod.lua` |
| `defaultEnabled` | boolean | no | `false` when `mod.lua` says `enabled = false`; leave out otherwise |
| `download` | object | no | `{ "url": ..., "version": ..., "sha256": ... }`: overrides the index's `download` for this mod |

## Kinds: mods, plugins and tools

An entry is a **mod** unless it says otherwise. `kind` makes an index able to offer the other things a player needs around the game, and the manager
sorts them into the tabs *Mods*, *Plugins* and *Tools*:

| `kind` | What it is | Where it goes | In the manager |
|---|---|---|---|
| `mod` (default) | A Sacred Gear mod: a folder with `mod.lua`. | `<game>\mods\<folder>` | On/Off switch, settings and key binds shown from `mod.lua` |
| `plugin` | Files for the game folder itself, for example `ddraw.dll` of Sacred-Bild or `SacredGear.dll`. | `<game>` (or a folder below it, see `install.to`) | Installed / not installed, no switch |
| `tool` | A program that runs on its own, for example the custom class editor. | The tools folder, one folder per tool (Settings) | **Start** button |

Extra fields of an entry (all optional; they do nothing for a mod):

| Field | Type | Meaning |
|---|---|---|
| `kind` | string | `mod`, `plugin` or `tool` (any case; an unknown kind skips the entry with a warning) |
| `platforms` | string[] | Runtime identifiers the entry is for: `win-x64`, `linux-x64`, or just `win` / `linux`. Empty: every platform. Entries for another platform are not shown. |
| `install.files` | array | Plugins: the files to copy. Each is a path inside the entry's folder in the zip (`"ddraw.dll"`), or `{ "from": "bin/ddraw.dll", "to": "ddraw.dll" }`; a folder name copies everything below it. Empty: every file of the entry's folder, keeping its relative path. |
| `install.to` | string | Plugins: a folder below the game folder the files go to (default: the game folder itself) |
| `install.launch` | string | Tools: the program to start, relative to the tool's folder |

`path` is the folder of the entry inside the zip. For a plugin or tool it may be empty (or missing): the root of the zip. `version` is whatever the
author calls the release; the manager compares versions of dotted numbers (`1.2.3`) to offer updates. `requires` may name entries of any kind (a mod that
needs a plugin, a tool that needs a mod), and the manager installs what is required first.

What the manager does with them, so that an index can be trusted to a point:

* **Plugins** only write below the game folder. A file that the player already has at a target is copied away first and **put back when the plugin is
  removed**. A plugin never replaces an existing program (`.exe`), `mods\gear_mods.json` or the manager's own record, and never a file that another
  plugin installed. The dialog before the install lists the files and says that originals are saved.
* **Tools** are programs: they are unpacked into the tools folder and started by the player. Removing a tool removes the files the manager wrote and keeps what
  the tool made itself unless the player asks to delete the folder.
* The game must not be running while mods or plugins change.

### Downloads that name a GitHub release

A fixed `releases/latest/download/<name>` only works when the asset has a fixed name. When the asset name carries a version or a commit
(`SacredGear-git-d9f303a72.zip`) or the release is a pre-release (nightlies), the download names the repository and a name pattern instead of a URL; the manager asks the
GitHub API for the newest matching release:

| Field of `download` | Meaning |
|---|---|
| `github` | `owner/repo` |
| `asset` | The asset name pattern: `*` is any text, `?` one character, case-insensitive, the whole name must match, e.g. `SacredGear-git-*.zip`. Several patterns can be joined with `\|`, and one starting with `!` excludes: `SacredBild-git-*.zip\|!*-debug.zip` |
| `prerelease` | `true` to also consider pre-releases (default: published releases only) |

The newest release that is not a draft (and not a pre-release, unless asked) with a matching asset wins. Its **tag** becomes the version of the download, and the version
of an entry that has none (`v1.2` is shown as `1.2`); the checksum GitHub publishes for the asset (`digest`) is checked after the download. `download.github` works in an
entry's own `download` and in the index's `download`.

### Examples

```json
{
  "id": "sacred-gear", "kind": "plugin", "name": "SacredGear.dll", "author": "kirides",
  "description": "The mod kit that loads the Sacred Gear mods.", "tags": ["framework"], "platforms": ["win"],
  "download": { "github": "kirides/sacred-bild", "asset": "SacredGear-git-*.zip", "prerelease": true },
  "install": { "files": ["SacredGear.dll"] }
},
{
  "id": "sacred-bild", "kind": "plugin", "name": "Sacred-Bild (ddraw.dll)", "author": "kirides", "platforms": ["win"],
  "description": "Replaces the game's ddraw.dll with the Sacred-Bild renderer.",
  "download": { "github": "kirides/sacred-bild", "asset": "SacredBild-git-*.zip|!*-debug.zip", "prerelease": true },
  "install": { "files": [ { "from": "ddraw.dll", "to": "ddraw.dll" } ] }
},
{
  "id": "class-editor", "kind": "tool", "name": "Custom class editor", "version": "1.0.0", "platforms": ["win"],
  "path": "", "download": { "url": "https://example.com/releases/class-editor-win-x64.zip" },
  "install": { "launch": "SacredClassEditor.exe" }, "requires": ["custom-classes"]
}
```

(The zip layouts are what these examples assume: `SacredGear.dll` and `ddraw.dll` at the root of their zips.) `tools/make_mod_index.py` keeps every entry whose `kind` is
`plugin` or `tool` when it regenerates the mods.

## Meta-index entry (`indexes[]`)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `url` | string | yes | URL of another `mod-index.json`: absolute https, or relative to the URL of the index that lists it |
| `name` | string | no | A label |

## Rules

- `path` is relative to the root of the zip, and so is a relative `thumbnail`. Both resolve against the download zip of
  the index that lists the mod (or the mod's own `download`, when it has one).
- `id` must equal the `id` in the mod's `mod.lua`; `name`, `version`, `author`, `description` and `requires` are copied
  from it. The CI step "Check mod-index.json" fails when a shipped mod folder is missing from the index, when a `path`
  has no folder in `src` or no `mod.lua`, when an id differs, or when the JSON is invalid.
- Which folders are shipped follows the release workflow: every `src/*/` except `*_selftest` and `sector-tools`.
- Indexes are followed recursively. Each distinct index URL (normalized) is visited only once, so cycles and duplicates
  are safe. A sub-index that fails to load is skipped with a warning. The manager caps the depth at 8 and the number of
  indexes at 64.
- Other repositories can publish their own `mod-index.json` the same way (a zip with `mods/<name>/...` plus the index as
  release assets) and be listed in someone's `indexes`.
- Unknown fields are ignored, so the format can grow without a new `schema`.

## Generating the index

`tools/make_mod_index.py` of the sacred-gear-mods repository (Python 3.8+, standard library only) builds the `mods` array from the `mod.lua` files, so
nobody has to copy versions by hand. It reads the top-level fields of the table `mod.lua` returns (it does not run
Lua), keeps the top-level fields, `indexes` and the hand-edited `tags`, `thumbnail` and `defaultEnabled` of the existing
file, and rewrites the rest:

```
python tools/make_mod_index.py            # update mod-index.json in place
python tools/make_mod_index.py --check    # CI: exit 1 with a diff when the file is out of date
```

The defaults fit this repository (`--src src`, `--prefix mods`, `--out mod-index.json`, skipping `*_selftest` and
`sector-tools`). For your own mods repository, pass for example `--name "My mods" --author me --homepage URL
--download-url URL --version latest --thumb-base https://raw.githubusercontent.com/me/repo/main --exclude 'wip_*'`.
The download URL defaults to the `releases/latest/download/sacred-gear-mods.zip` of the `origin` remote. A thumbnail is
taken from `thumbnail.png|jpg|webp` inside the mod folder, or from `docs/images/<id>.png|jpg|webp` when `--thumb-base`
is given.

## Example

```json
{
  "schema": 1,
  "name": "Sacred Gear Mods",
  "author": "kirides",
  "description": "...",
  "homepage": "https://github.com/kirides/sacred-gear-mods",
  "download": {
    "url": "https://github.com/kirides/sacred-gear-mods/releases/latest/download/sacred-gear-mods.zip",
    "version": "latest"
  },
  "mods": [
    {
      "id": "bounty",
      "path": "mods/bounty",
      "name": "Bounty board",
      "version": "0.4.0",
      "author": "Sacred Gear",
      "description": "...",
      "tags": ["quests"],
      "thumbnail": "https://raw.githubusercontent.com/kirides/sacred-gear-mods/main/docs/images/bounty.jpg",
      "images": [
        "https://raw.githubusercontent.com/kirides/sacred-gear-mods/main/docs/images/bounty.jpg",
        "mods/bounty/screenshots/board.png"
      ]
    }
  ],
  "indexes": [
    { "url": "https://example.com/other-mods/mod-index.json", "name": "Other mods" }
  ]
}
```

This repository's own index has no `indexes`.
