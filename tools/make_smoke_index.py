"""Writes a tiny local index and package for the headless self test of a built program (used by the nightly workflow on every platform):

    python tools/make_smoke_index.py <dir>
    "<publish>/Gear Mod Manager" --selftest <dir>/mod-index.json

The package holds two mods (one requires the other), a plugin and a tool, so that the self test installs, switches, removes and restores
them with the real Native AOT binary: Lua sandbox, zip, JSON and the file work on that platform.
"""
import json, os, sys, zipfile


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "smoke")
    os.makedirs(out, exist_ok=True)

    def lua(mod_id, requires=None):
        req = "requires = {%s}," % ", ".join("'%s'" % r for r in requires) if requires else ""
        return ("return {\n    id = '%s',\n    name = 'Smoke %s',\n    version = '1.0.0',\n    %s\n"
                "    settings = {{id = 'on', type = 'bool', label = 'On', default = true}},\n    keys = {{id = 'panel', label = ('Open the %%s'):format('panel'), default = 'f5'}},\n}\n") % (mod_id, mod_id, req)

    with zipfile.ZipFile(os.path.join(out, "pkg.zip"), "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("mods/smoke-lib/mod.lua", lua("smoke-lib"))
        z.writestr("mods/smoke-lib/main.lua", "-- lib\n")
        z.writestr("mods/smoke-app/mod.lua", lua("smoke-app", ["smoke-lib"]))
        z.writestr("mods/smoke-app/main.lua", "-- app\n")
        z.writestr("plugins/smoke-plugin/smoke.dll", b"MZ smoke")
        z.writestr("tools/smoke-tool/tool.txt", "smoke tool\n")

    index = {
        "schema": 1, "name": "Smoke", "author": "ci", "download": {"url": "pkg.zip", "version": "1"},
        "mods": [
            {"id": "smoke-lib", "path": "mods/smoke-lib", "name": "Smoke lib", "version": "1.0.0"},
            {"id": "smoke-app", "path": "mods/smoke-app", "name": "Smoke app", "version": "1.0.0", "requires": ["smoke-lib"]},
            {"id": "smoke-plugin", "kind": "plugin", "path": "plugins/smoke-plugin", "name": "Smoke plugin", "version": "1.0.0", "install": {"files": ["smoke.dll"]}},
            {"id": "smoke-tool", "kind": "tool", "path": "tools/smoke-tool", "name": "Smoke tool", "version": "1.0.0", "install": {"launch": "tool.txt"}},
        ],
    }
    with open(os.path.join(out, "mod-index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, indent=2)
    print(os.path.join(out, "mod-index.json"))


if __name__ == "__main__":
    main()
