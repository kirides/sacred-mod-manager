# gear-mod-manager:// links

The manager registers itself as the handler of the `gear-mod-manager://` scheme, so a web page, a README or a chat message can add a mod source or
install a mod with one click. **The manager never acts on a link without asking**: it shows what the link wants (the address of the source, the mod,
its version and author, what it needs) and waits for a yes. A link can only point at **https** addresses, never at a local file.

## The links

| Link | What it does |
|---|---|
| `gear-mod-manager://add-index?url=<address>` | Asks whether to add the mod index at `<address>` as a source (also `add`). The address is the https URL of a `mod-index.json`, or a GitHub repository address (`https://github.com/owner/repo`). |
| `gear-mod-manager://install?mod=<id>&index=<address>` | Adds the index when the player does not have it yet (one question for both), then asks whether to install the mod `<id>` (and what it requires). |
| `gear-mod-manager://install?mod=<id>` | The same for a mod of a source the player already has. |
| `gear-mod-manager://activate` | Brings the window to the front (what a second start sends). |

The values are percent-encoded like any URL query (`https%3A%2F%2Fgithub.com%2Fkirides%2Fsacred-gear-mods`). `:` and `:///` after the scheme work as well as
`://`, the action is case-insensitive, the first of two equal keys wins, a link is at most 2048 characters. An unknown action, a missing `url` or `mod`, an
invalid mod id (letters, digits, `_`, `-`), a non-https address or an address that is a local path gives a dialog that says why and does nothing.

The manager builds them for you: **Copy link** in the detail pane of a mod (Browse) and next to each source (Sources) put the right link on the clipboard.
For a web page:

```html
<a href="gear-mod-manager://add-index?url=https%3A%2F%2Fraw.githubusercontent.com%2Fkirides%2Fsacred-gear-mods%2Fmain%2Fmod-index.json">Add the Sacred Gear mods</a>
<a href="gear-mod-manager://install?mod=bounty&index=https%3A%2F%2Fraw.githubusercontent.com%2Fkirides%2Fsacred-gear-mods%2Fmain%2Fmod-index.json">Install the bounty board</a>
```

GitHub's Markdown does not render links with unknown schemes, so a README links to a page of your own (for example GitHub Pages) that holds the `<a href>`.

## Install banners (for page authors)

An *install banner* is a button or link on a page that uses one of the links above. A player who has started the manager once clicks it, the browser asks to open Gear Mod Manager, and the
manager asks the player to confirm. Make one for your own project page (GitHub Pages, a forum post, a Discord pin ...):

```html
<a href="gear-mod-manager://add-index?url=https%3A%2F%2Fgithub.com%2Fkirides%2Fsacred-bild"
   style="display:inline-block;padding:10px 18px;border-radius:8px;background:#2F6FB0;color:#fff;font:600 15px sans-serif;text-decoration:none">
  Add Sacred-Bild, Sacred Gear and the mods to Gear Mod Manager
</a>
```

* `add-index?url=` takes the https address of your `mod-index.json`, or just the address of the GitHub repository that holds it (percent-encoded: `%3A` for `:`, `%2F` for `/`). The manager follows
  the other indexes your index points to, so one banner can bring a whole family of mods; the Sacred-Bild index is the example (its plugins `sacred-bild` and `sacred-gear`, and a pointer to the mods).
* `install?mod=<id>&index=<address>` is a banner for **one** item: the manager adds the source if the player does not have it yet (one question for both) and asks to install the item.
* GitHub's Markdown removes links with unknown schemes, so a README cannot hold the banner itself. Put it on a page you host and link to that page from the README, e.g.
  `[![Add to Gear Mod Manager](https://img.shields.io/badge/Gear%20Mod%20Manager-add%20source-2F6FB0)](https://your-page.example/install.html)`.
* **Copy link** in the manager (a source on the Sources page, a mod in Browse) writes the correct link for you.

## Registration

Only for the **current user**, no administrator rights, and only when the player has *Open gear-mod-manager:// links with this program* on (Settings, on by default).

* **Windows**: `HKEY_CURRENT_USER\Software\Classes\gear-mod-manager` with `URL Protocol`, `DefaultIcon` and `shell\open\command` = `"<path>\Gear Mod Manager.exe" "%1"`.
* **Linux**: `~/.local/share/applications/gear-mod-manager.desktop` (`Exec="<path>/Gear Mod Manager" %u`, `MimeType=x-scheme-handler/gear-mod-manager;`), then
  `xdg-mime default gear-mod-manager.desktop x-scheme-handler/gear-mod-manager` and `update-desktop-database` (both best effort). Build the Linux binary with
  `dotnet publish src/SacredModManager -c Release -r linux-x64`. The Linux code path is covered by a test that only runs on Linux; it has not been run on a Linux machine yet.
* Other systems: not supported, the Settings card is hidden.

At start the manager registers itself only when nothing is registered. If another copy of the manager is registered (a second install, a build folder), it is left alone;
Settings shows that and offers **Use this copy for links**. Switching the setting off removes the registration again.

## One manager at a time

When the system starts the program for a link and a manager already runs, the new process hands the link to the running one (a named pipe, current user only,
guarded by a lock file in the manager's data folder) and ends within milliseconds; the running window comes to the front and asks. A plain second start
just brings the first window forward. If the running manager does not answer, the new process starts normally.
