"""Builds a throw-away test environment for the manager, so nothing real is ever touched.

    python tools/make_test_env.py <out dir> [--mods-repo <sacred-gear-mods>] [--copy-mods <game>\\mods]

Creates in <out dir>:
  game\\Sacred.exe      an empty stub (the manager only checks that it exists)
  game\\mods\\          optional copy of a real mods folder (--copy-mods), else empty
  source\\mod-index.json + sacred-gear-mods.zip   a local index built from the mods repo like the release workflow does
  home\\                the manager's settings and data (SACRED_MODMANAGER_HOME)
Prints the command line to start the manager against it.
"""
import argparse, json, os, shutil, struct, sys, zipfile, zlib

SKIP = ("_selftest", "sector-tools")


def png(w, h, fn):
    """A small PNG without any imaging library: fn(x, y) -> (r, g, b)."""
    raw = b"".join(bytes([0]) + b"".join(bytes(fn(x, y)) for x in range(w)) for y in range(h))

    def chunk(kind, data):
        c = struct.pack(">I", len(data)) + kind + data
        return c + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    signature = bytes([0x89]) + b"PNG" + bytes([0x0D, 0x0A, 0x1A, 0x0A])
    return signature + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b"")


def pictures():
    """Three different 640x360 test pictures."""
    return [
        png(640, 360, lambda x, y: (40 + x * 160 // 640, 90 + y * 120 // 360, 170)),
        png(640, 360, lambda x, y: (170, 60 + x * 150 // 640, 60 + y * 100 // 360)),
        png(640, 360, lambda x, y: (60 + (x ^ y) % 120, 150, 80 + (x + y) % 100)),
    ]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--mods-repo", default=r"E:\git\github\sacred-gear-mods")
    ap.add_argument("--copy-mods")
    ap.add_argument("--thumb", action="store_true", help="put generated pictures into some mods (thumbnail + images array) to test zip-relative pictures")
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
    pics = pictures() if a.thumb else None
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
            if pics and len(name) % 2 == 0:
                count = 1 + len(name) % 3  # 1 to 3 pictures
                for i in range(count):
                    z.writestr(f"mods/{name}/images/{i + 1}.png", pics[i])
                for m in index.get("mods", []):
                    if m["path"] == f"mods/{name}":
                        m["thumbnail"] = f"mods/{name}/images/1.png"
                        m["images"] = [f"mods/{name}/images/{i + 1}.png" for i in range(count)]
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
