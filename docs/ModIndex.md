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
