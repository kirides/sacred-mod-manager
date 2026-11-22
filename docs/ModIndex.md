# Mod index

A `mod-index.json` lists the mods of a release for desktop mod managers (the Gear Mod Manager reads it). The index of the
Sacred Gear mods is the reference example. It is plain JSON, UTF-8 without BOM. The release workflow publishes it as a release asset next to
`sacred-gear-mods.zip`, so the stable URLs are:

- `https://github.com/kirides/sacred-gear-mods/releases/latest/download/mod-index.json`
- `https://github.com/kirides/sacred-gear-mods/releases/latest/download/sacred-gear-mods.zip`

The zip holds `mods/<name>/...` (one folder per mod, ready to extract into the game folder) and `README.txt`.

> **The rule: an index is purely declarative. Nothing is ever run.** An index (and a package) says what to download and which files to copy where, nothing else. No `.exe`, `.msi`,
> `.bat`, `.cmd`, `.ps1`, `.sh` or any other installer or script step, no "post-install" hook, no shell: there is no field for it and there never will be. Fields named like `run`,
> `exec`, `script`, `command`, `installer`, `hook`, `postInstall` ... are ignored and the loader says so in its warnings. Dependencies are expressed with `requires` and `indexes[]`, never with code.
>
> Why: (a) the player sees **exactly what is installed before it happens**: the confirmation lists, for every mod, plugin and tool, where it comes from and every file or folder with
> its target (and a swap between plugins that own the same file is a question that names the files and the owner, with nothing changed before the answer). A script could do things
> that list cannot show. (b) The manager can always tell **which mod or plugin provided which file** in the game folder: the install record maps every file it wrote to exactly one
> owner, with the SHA-256 it wrote, and nothing writes a file the manager does not track. An installer would write files behind its back.
>
> The places where the manager starts a process at all are all started by the player's own click and are listed in [ProcessAudit.md](ProcessAudit.md); a test keeps that list exact.

## Index (schema 1)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `schema` | number | yes | Format version, `1` |
| `name` | string | yes | Name of the index / publisher |
| `author` | string | yes | Author of the index |
| `description` | string | no | One or two sentences |
| `homepage` | string | no | https URL of the project |
| `download` | object | no | The zip the `mods` come from: `url` (https, or relative to the index), `version` (the latest tag, or `latest`) and optionally `sha256` (hex of the zip; the manager rejects a download that does not match) and `inner` (see below) |
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
| `requires` | string[] | no | What this one needs: ids (copied from `mod.lua`), each optionally with a minimum version, `"sacred-gear>=0.4.1"`. See [Minimum versions](#minimum-versions) |
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
| `install.files` | array | Plugins: the files to copy. Each is a path inside the entry's folder in the zip (`"ddraw.dll"`), or `{ "from": "bin/ddraw.dll", "to": "ddraw.dll" }`; a folder name copies everything below it; `from` may contain wildcards (see below). Empty: every file of the entry's folder, keeping its relative path. |
| `install.to` | string | Plugins: a folder below the game folder the files go to (default: the game folder itself) |
| `install.launch` | string | Tools: the program to start, relative to the tool's folder |

### Archive formats

A download (and a local file the player opens, drops onto the window or passes on the command line) can be a `.zip`, a `.tar`, a `.tar.gz` / `.tgz` or a `.tar.zst`. The manager recognises the format by the
first bytes of the file, not by its name or the URL's extension (a zip served as `x.tar.gz` still works). Everything in this document that says "zip"
(`path`, `thumbnail`, `install.files`, `mod.lua` lookup, `sha256`) applies to every format the same way. Limits and checks are the same for all formats:
at most 20,000 files, 200 MB per file, 600 MB per mod, counted on the bytes really read.

There is one input stage for all of this (`ArchiveFormats` / `ArchivePackage` in the Core project): the package cache, downloads, install, thumbnails, the file picker, drag and drop and
the command line all use it, and the cache file is named by the URL only (`.pkg`), never by a format. A further format (for example `tar.xz`) is added by registering one
`ArchiveFormat` there (how to recognise it, its extensions, how to decompress); nothing else changes.

### Two plugins that own the same file

Nothing in the index declares this. When the manager installs a plugin and finds that a target file is owned by another installed plugin, it names that plugin and asks:
remove it and install this one, or cancel (nothing is changed before the answer). Use this for variants of one file, e.g. two `alsoft.ini` plugins that both `requires` the same base plugin.

A tar-based archive is checked completely when it is opened, and one bad entry makes the whole archive unusable:

- only regular files and folders are accepted; symbolic links, hard links, devices, fifos and any other entry type are rejected (no link is ever followed);
- a file name must be relative and plain: no `..`, no leading `/`, no drive (`C:`) or `:` at all, no empty segment (`a//b`), no Windows device name (`CON`, `NUL`, `COM1`...), no trailing dot or space, at most 240 characters; a leading `./` is dropped and `\` counts as `/`;
- two entries with the same name (ignoring case) are rejected;
- a gzip or zstd stream that unpacks to more than 2 GB is rejected while scanning.

### Wildcards in `install.files[].from`

`from` can name many files at once. The rules are small on purpose:

- `*` matches any run of characters and `?` exactly one, inside **one** path segment (never across `/`). Matching ignores case.
- A segment that is exactly `**` stands for any number of folders, also none: `**` is every file, `bin/**` every file below `bin`, `**/*.dll` every `.dll` at any depth. `**` inside a longer segment (`a**b`) is invalid.
- A pattern is matched only against the names of files already found in the entry's folder of the archive. It cannot name anything else, and a pattern with `..`, `.`, an empty segment, a leading `/` or a `:` is rejected (`\` is read as `/`, as in plain paths; the entry is skipped with a warning), exactly like a plain path.
- When there is more than one match, `to` is a **target folder** (below `install.to`), not a file name. The literal folders at the start of the pattern are its base; a match is written to `to/<path below the base>`. So `*.dll` puts every dll flat into `to`, `data/**` keeps the layout below `data/`, and `lib/*/x.dll` writes `<folder>/x.dll`.
- Without `to` (the string form `"*.dll"`) the target folder is the pattern's base: the game folder (or `install.to`) for `*.dll`, `bin` for `bin/**`.
- `to` itself never contains a wildcard. A pattern that matches nothing makes the install fail with a message, and two files that would land on the same target are an error.

```json
"install": { "to": "mods", "files": [ "*.dll", { "from": "data/**", "to": "gamedata" }, { "from": "**/*.cfg", "to": "config" } ] }
```

`path` is the folder of the entry inside the zip. For a plugin or tool it may be empty (or missing): the root of the zip. `version` is whatever the
author calls the release; the manager compares versions of dotted numbers (`1.2.3`) to offer updates. `requires` may name entries of any kind (a mod that
needs a plugin, a tool that needs a mod), and the manager installs what is required first.

### Minimum versions

An entry of `requires` is an id, or an id with a minimum version: `"sacred-gear>=0.4.1"` (spaces around `>=` are fine). A plain id keeps its meaning, so
indexes without constraints work as before. Versions are compared as dotted numbers
(`0.10.0` is newer than `0.4.1`; a leading `v` is ignored).

* **Install** (and update): the manager checks what is installed against the minimum. A requirement that is too old is **updated first** when a source offers a
  version that is new enough. If none does, **nothing is installed** and the dialog says which mod needs which version, what the player has and what the sources offer.
  A requirement that is not installed is installed first, the same way.
* **Switching on**: a mod stays off, with a message, while something it needs is installed in a version below its minimum.
* **Lists**: the requirement shows its minimum and whether the installed version meets it.
* A version that is not known (an item installed by hand, or an entry without `version`) cannot be compared and counts as met.
* Only the index says this. `mod.lua` keeps plain ids: Sacred Gear reads its `requires` as exact ids, so write the constraint in the index (the generator of
  your index can add it). A constraint the manager cannot read (`"a>1.0"`) is ignored with a warning in the sources list.
* An older manager that does not know the syntax finds no mod with the id `sacred-gear>=0.4.1`, drops the entry and installs the mod without the check.

```json
{ "id": "my-ui", "path": "mods/my-ui", "requires": ["sacred-gear>=0.4.1", "my-theme"] }
```

The id (`sacred-gear` here) is looked up in all loaded indexes like any other, so the index that offers the plugin must be one of the player's sources for the
update to be offered.

What the manager does with them, so that an index can be trusted to a point:

* **Plugins** only write below the game folder. A file that the player already has at a target is copied away first and **put back when the plugin is
  removed**. A plugin never replaces an existing program (`.exe`), `mods\gear_mods.json` or the manager's own record, and never a file that another
  plugin installed. The dialog before the install lists the files and says that originals are saved.
* **Tools** are programs: they are unpacked into the tools folder and started by the player. Removing a tool removes the files the manager wrote and keeps what
  the tool made itself unless the player asks to delete the folder.
* The game must not be running while mods or plugins change.

### Loose files (a plugin file without an archive)

A plugin whose files are tiny (an `.ini`, a text file) can name each file by an https address instead of shipping an archive. The entry has no `download` and no `path`;
every `install.files` entry is an object with `url`, `sha256` and `to`:

```json
{
  "id": "sacred-dsoal-speakers",
  "kind": "plugin",
  "name": "sacred-dsoal (speakers)",
  "requires": ["sacred-dsoal"],
  "install": {
    "files": [
      { "url": "https://raw.githubusercontent.com/<owner>/<repo>/<commit>/docs/dsoal/alsoft-speakers.ini",
        "sha256": "<64 hex digits of the file>",
        "to": "alsoft.ini" }
    ]
  }
}
```

Rules (the manager enforces all of them, the index cannot relax them):

* `url` is an absolute **https** address, no credentials in it. A redirect is followed only to another https address (at most 5 hops); a redirect to http is refused.
* `sha256` is **mandatory**: the bytes must match, otherwise nothing is installed. A missing or malformed hash makes the loader skip the entry (with a warning). Pin the file to a
  commit address, not a branch, so the hash stays valid.
* `to` is a path below the game folder (below `install.to` when given): no `..`, no absolute path, no drive, no wildcard.
* At most **1 MB** per file.
* A loose file is **never a program or script**: `.exe .com .scr .msi .msp .bat .cmd .ps1 .psm1 .psd1 .vbs .vbe .js .jse .wsf .wsh .hta .lnk .reg .cpl .jar .sh .pif .gadget .inf .url` are refused.
* An entry has either loose files or archive files, not both, and no per-entry archive `download`.
* The question before the install lists each file with its URL, hash and target. Afterwards the install record names the plugin as the owner of the file with its hash, so the
  usual rules apply unchanged: one owner per file (another plugin owning the target gives the swap question), a file the player already had is saved and put back on removal.
* Nothing is run. Unknown fields stay ignored.

### An archive inside the download

Some releases are wrapped: the downloaded archive holds one more archive (the official DSOAL release `DSOAL_r697.zip` holds `DSOAL_r697.zip`). `download.inner` names that
entry; the manager unpacks only it (at most 64 MB, to a temporary file that is deleted afterwards) and reads `path` and `install.files` from it. The `sha256` is still the one of
the download. The install question says "the archive ... inside it". Nothing inside is run.

```json
"download": { "url": "https://github.com/kcat/dsoal/releases/download/archive/DSOAL_r697.zip", "sha256": "<hex>", "inner": "DSOAL_r697.zip" }
```

### Programs and scripts in archive plugins

A plugin writes a file with one of the extensions above only when its entry names that file in `install.files` (without wildcard). "Every file" (empty `install.files`), a folder
name or a wildcard that would bring one in is refused, and so the player is never surprised by a script in the game folder. DLLs, ini and data files are not affected.

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

### Dependencies across files: the sibling index pattern

Complex dependencies need no code: an index can list other indexes, for example separate JSON files in the same repository named after the dependency. A relative `url` is
resolved against the address of the index that lists it (an https address or a file on disk, nested as deep as the limits allow), and a relative `download.url` inside the
sub-index is resolved against the sub-index. `requires` ids are looked up in **all** loaded indexes, so a mod of the main index can require a plugin that lives in a sibling file.

`mod-index.json`:

```json
{ "schema": 1, "name": "My mods", "indexes": [ { "url": "sacred-dsoal.json" } ],
  "download": { "url": "my-mods.zip" },
  "mods": [ { "id": "my-mod", "path": "mods/my-mod", "requires": ["sacred-dsoal-speakers"] } ] }
```

`sacred-dsoal.json` (beside it):

```json
{ "schema": 1, "name": "DSOAL", "download": { "url": "sacred-dsoal-v1.0.0.zip" }, "mods": [
  { "id": "sacred-dsoal", "kind": "plugin", "path": "base", "install": { "files": ["dsound.dll", "dsoal-aldrv.dll"] } },
  { "id": "sacred-dsoal-speakers", "kind": "plugin", "path": "speakers", "requires": ["sacred-dsoal"], "install": { "files": ["alsoft.ini"] } },
  { "id": "sacred-dsoal-headphones", "kind": "plugin", "path": "headphones", "requires": ["sacred-dsoal"], "install": { "files": ["alsoft.ini"] } } ] }
```

The limits are the same as for any `indexes[]` (each index is read once, cycles and duplicates cost nothing, at most 8 levels and 64 indexes).

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
