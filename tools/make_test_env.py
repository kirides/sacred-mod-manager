"""Builds a throw-away test environment for the manager, so nothing real is ever touched.

    python tools/make_test_env.py <out dir> [--mods-repo <sacred-gear-mods>] [--copy-mods <game>\\mods]

Creates in <out dir>:
  game\\Sacred.exe      an empty stub (the manager only checks that it exists)
  game\\mods\\          optional copy of a real mods folder (--copy-mods), else empty
  source\\mod-index.json + sacred-gear-mods.zip   a local index built from the mods repo like the release workflow does
  home\\                the manager's settings and data (SACRED_MODMANAGER_HOME)
Prints the command line to start the manager against it.
"""
import argparse, json, os, shutil, sys, zipfile

SKIP = ("_selftest", "sector-tools")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--mods-repo", default=r"E:\git\github\sacred-gear-mods")
    ap.add_argument("--copy-mods")
    ap.add_argument("--thumb", help="png put into some mods as thumbnail.png to test zip-relative thumbnails")
    a = ap.parse_args()
    out = os.path.abspath(a.out)
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(os.path.join(out, "game", "mods"))
    os.makedirs(os.path.join(out, "source"))
    os.makedirs(os.path.join(out, "home"))
    open(os.path.join(out, "game", "Sacred.exe"), "wb").write(b"MZ")
    if a.copy_mods:
        shutil.copytree(a.copy_mods, os.path.join(out, "game", "mods"), dirs_exist_ok=True)

    src = os.path.join(a.mods_repo, "src")
    index = json.load(open(os.path.join(a.mods_repo, "mod-index.json"), encoding="utf-8"))
    zip_path = os.path.join(out, "source", "sacred-gear-mods.zip")
    thumb = open(a.thumb, "rb").read() if a.thumb else None
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("README.txt", "test package\n")
        for name in sorted(os.listdir(src)):
            d = os.path.join(src, name)
            if not os.path.isfile(os.path.join(d, "mod.lua")) or name.endswith(SKIP[0]) or name == SKIP[1]:
                continue
            for root, dirs, files in os.walk(d):
                dirs[:] = [x for x in dirs if x not in ("tests", "tools")]
                for f in files:
                    full = os.path.join(root, f)
                    z.write(full, "mods/" + os.path.relpath(full, src).replace(os.sep, "/"))
            if thumb and len(name) % 2 == 0:
                z.writestr(f"mods/{name}/thumbnail.png", thumb)
                for m in index.get("mods", []):
                    if m["path"] == f"mods/{name}":
                        m["thumbnail"] = f"mods/{name}/thumbnail.png"
    for m in index.get("mods", []):
        t = m.get("thumbnail", "")
        if t.startswith("http"):
            del m["thumbnail"]  # raw.githubusercontent urls are not reachable from the test environment
    index["download"] = {"url": "sacred-gear-mods.zip", "version": "test"}
    index.pop("indexes", None)
    json.dump(index, open(os.path.join(out, "source", "mod-index.json"), "w", encoding="utf-8"), indent=2)

    exe = r"src\SacredModManager\bin\Debug\net10.0\win-x64\SacredModManager.exe"
    print("SACRED_MODMANAGER_HOME=" + os.path.join(out, "home"))
    print(f'{exe} --game "{os.path.join(out, "game")}" --source "{os.path.join(out, "source", "mod-index.json")}"')


if __name__ == "__main__":
    sys.exit(main())
