# Process audit: what the manager starts, and what it never runs

Rule (see [ModIndex.md](ModIndex.md)): indexes and packages are declarative. Installing, updating and removing a mod, plugin or tool **only copies and deletes files**. Nothing from a package is
started, loaded or evaluated by the manager during these steps, and no index field can ask for it.

## Install paths (audited)

| Path | What it does with package content | Runs anything? |
|---|---|---|
| Archive reader (`ArchiveFormats`, `ArchivePackage`, zip/tar backends) | Lists names, checks them, copies bytes to a staging folder below the manager's backup folder | No |
| Mods (`ModStore.Install`) | Copies the mod's folder into `mods\`. Reads `mod.lua` as **text** in an empty Lua state (no libraries, instruction/memory/time limits) to get the name, version and requirements. This is a sandboxed evaluation of the manifest, not a program start; the mod's other Lua files are run by the **game** (Sacred Gear), never by the manager | Manifest only, sandboxed |
| Plugins (`ModStore.InstallPlugin`) | Copies the mapped files into the game folder; refuses to replace an existing `.exe`, a file another plugin owns (the player decides, see Features), and paths outside the game folder. A DLL plugin is code that the **game** loads at its next start: that is what a plugin is for | No |
| Tools (`ModStore.InstallTool`) | Unpacks files into the tools folder | No. `install.launch` is only stored |
| Updates / removal / restore / undo | Copy and delete files recorded in the install record | No |
| Index loader | Parses JSON into plain data; unknown fields are never acted on, run-like fields produce a warning | No |
| Protocol / link handling (`gear-mod-manager://`) | Parses the link, asks the player, then adds a source or installs a catalog item through the paths above; opening a local archive from the command line goes through the same install check | No |

## Every place where the manager starts a process

All of them follow a click or a deliberate action of the player, none of them runs during install, update or removal, none takes a command line from an index or package.

1. **Start Sacred** (`GameLocator.Launch`): starts `Sacred.exe` in the game folder.
2. **Tool Start button** (`MainWindowViewModel.LaunchTool`): starts the program named by the tool's `install.launch`, resolved inside the tool's own folder. The player clicks it **after** the install. *Open question for the maintainer: whether this stays. It is untouched.*
3. **Open folder / open page** (`MainWindowViewModel.OpenPath`): asks the system to open a folder (mods folder, a mod's folder, the data folder) in the file manager, or the update web page in the browser. Package content is not opened.
4. **Link registration on Linux** (`UriSchemeRegistrar.RunQuietly`): runs `update-desktop-database` with the applications folder as its argument.

Reading only (no start): `GameLocator` looks at the running processes by name to know whether `Sacred.exe` runs.

`NoExecutionTests` keeps this list exact (a new place that starts a process fails the test until it is added here), proves that a package full of `.exe`, `.msi`, `.bat`, `.cmd`, `.ps1`, `.sh`, `.vbs`, `.js`, `.lnk` files and an index that asks to run them installs only the mapped files and runs nothing, and that the install, archive and index code references no process or native-loading API.

## Recommendation: executable targets for plugins

Today a plugin may not replace an existing `.exe` or a file of another plugin, but it can write a new `.exe`, `.bat`, `.ps1`... into the game folder when its mapping (or an empty mapping = every file) says so. Nothing runs them, but a mapped script in the game folder is a trap for the player. Recommendation (not implemented, needs a decision): for plugins refuse targets with the extensions `.exe .com .scr .msi .bat .cmd .ps1 .psm1 .vbs .vbe .js .jse .wsf .hta .lnk .reg .cpl .jar .sh` unless the entry names the file explicitly in `install.files` (never through "every file" or a wildcard), and show those files in the install question. DLLs, ini and data files stay as they are, because they are what plugins are made of.
