#!/usr/bin/env python3
"""Builds the web demo's Noto fonts from the pinned archives in eng/webdemo/noto-fonts.json.

The demo serves these files from its own origin and fetches each one only when a page needs a
glyph from it. The font files are not committed: this script downloads every archive from the
URL in the manifest, checks the SHA-256 of the archive and of the file taken from it (it stops on
a mismatch), and writes into OUT:

  - the script and symbol faces, unchanged;
  - each CJK face cut into Unicode slices: a core slice (the common characters of the
    language's national character set, kana, hangul and CJK punctuation), then the rest of the
    face's characters in code point order, cjkSliceSize characters per slice;
  - index.json: every file with the code points it covers (see ranges()), which the app
    reads before it asks for a Noto face;
  - the licence of each source as OFL-<id>.txt.

Usage:
  python3 -I eng/webdemo/noto_fonts.py requirements > noto-requirements.txt
  python3 -m pip install --require-hashes --no-deps -r noto-requirements.txt
  python3 -I eng/webdemo/noto_fonts.py build --cache <download dir> --out <site>/fonts/noto

Only the slicing and the coverage need fontTools (the pinned version in the manifest).
"""

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import sys
import urllib.parse
import urllib.request
import zipfile

MANIFEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "noto-fonts.json")
ALLOWED_HOSTS = {"github.com", "files.pythonhosted.org"}
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")


def load_manifest(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def fail(message):
    print("error: " + message, file=sys.stderr)
    sys.exit(1)


def download(url, expected_sha, cache_dir):
    """The archive's bytes, from the cache when the cached copy has the pinned hash."""
    host = urllib.parse.urlparse(url).hostname
    if urllib.parse.urlparse(url).scheme != "https" or host not in ALLOWED_HOSTS:
        fail(f"{url}: only https downloads from {sorted(ALLOWED_HOSTS)} are allowed")
    name = os.path.basename(urllib.parse.urlparse(url).path)
    if not SAFE_NAME.match(name):
        fail(f"{url}: unexpected file name")
    cached = os.path.join(cache_dir, name)
    if os.path.exists(cached):
        with open(cached, "rb") as f:
            data = f.read()
        if sha256(data) == expected_sha:
            return data
    print(f"downloading {url}", file=sys.stderr)
    with urllib.request.urlopen(url, timeout=300) as response:
        data = response.read()
    actual = sha256(data)
    if actual != expected_sha:
        fail(f"{url}: SHA-256 is {actual}, the manifest pins {expected_sha}")
    os.makedirs(cache_dir, exist_ok=True)
    with open(cached, "wb") as f:
        f.write(data)
    return data


def member(archive, name, expected_sha, what):
    """One file of the archive by its exact name (read, never extracted to a path the archive chooses)."""
    try:
        data = archive.read(name)
    except KeyError:
        fail(f"{what}: {name} is not in the archive")
    actual = sha256(data)
    if actual != expected_sha:
        fail(f"{what}: {name} has SHA-256 {actual}, the manifest pins {expected_sha}")
    return data


def ranges(code_points):
    """
    Code points as runs, delta-encoded for a small index: a flat list of pairs [gap, extra], where
    a run starts gap code points after the end of the previous run (the first after -1) and holds
    extra + 1 code points.
    """
    runs = []
    for cp in sorted(code_points):
        if runs and runs[-1][1] == cp - 1:
            runs[-1][1] = cp
        else:
            runs.append([cp, cp])
    out, end = [], -1
    for first, last in runs:
        out.extend([first - end - 1, last - first])
        end = last
    return out


def char_count(encoded):
    return sum(encoded[i + 1] + 1 for i in range(0, len(encoded), 2))


def double_byte(codec, leads, trails):
    """The characters a double-byte character set encodes in rows `leads`."""
    found = set()
    for lead in leads:
        for trail in trails:
            try:
                text = bytes([lead, trail]).decode(codec)
            except UnicodeDecodeError:
                continue
            if len(text) == 1:
                found.add(ord(text))
    return found


def core_set(lang):
    """Common characters of a CJK language: its national set's symbols and first level, kana, punctuation."""
    common = set(range(0x3000, 0x3040)) | set(range(0x3040, 0x3100)) | set(range(0x31F0, 0x3200)) | set(range(0xFF00, 0xFFF0))
    if lang == "sc":    # GB 2312: symbol rows 1-9, level 1 hanzi rows 16-55
        common |= double_byte("gb2312", list(range(0xA1, 0xAA)) + list(range(0xB0, 0xD8)), range(0xA1, 0xFF))
    elif lang == "tc":  # Big5: symbols, then the 5401 frequently used characters (A440-C67E)
        common |= double_byte("big5", range(0xA1, 0xC7), list(range(0x40, 0x7F)) + list(range(0xA1, 0xFF)))
    elif lang == "jp":  # JIS X 0208: non-kanji rows 1-8, level 1 kanji rows 16-47
        common |= double_byte("euc_jp", list(range(0xA1, 0xA9)) + list(range(0xB0, 0xD0)), range(0xA1, 0xFF))
    elif lang == "kr":  # KS X 1001: symbol and jamo rows, 2350 hangul syllables
        common |= double_byte("euc_kr", list(range(0xA1, 0xAD)) + list(range(0xB0, 0xC9)), range(0xA1, 0xFF))
        common |= set(range(0x3130, 0x3190))
    else:
        fail(f"unknown CJK language {lang}")
    return common


def slice_cjk(font_bytes, lang, stem, slice_size, out_dir):
    """Writes the slices of one CJK face; returns (file, code points) for each."""
    from fontTools import subset
    from fontTools.ttLib import TTFont

    font = TTFont(io.BytesIO(font_bytes))
    cmap = set(font.getBestCmap().keys())
    core = sorted(core_set(lang) & cmap)
    rest = sorted(cmap - set(core))
    groups = [core] + [rest[i:i + slice_size] for i in range(0, len(rest), slice_size)]

    options = subset.Options()
    options.layout_features = []      # glyphs by cmap only: the renderer does not shape
    options.drop_tables += ["GSUB", "GPOS", "GDEF", "BASE", "VORG", "vhea", "vmtx", "DSIG"]
    options.hinting = False
    options.notdef_outline = True
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    options.name_legacy = True
    options.glyph_names = False
    options.recalc_timestamp = False

    written = []
    for n, group in enumerate(groups):
        part = TTFont(io.BytesIO(font_bytes))
        subsetter = subset.Subsetter(options)
        subsetter.populate(unicodes=group)
        subsetter.subset(part)
        name = f"{stem}-{n:02d}.otf"
        part.save(os.path.join(out_dir, name))
        written.append((name, group))
    return written


def coverage(font_bytes):
    from fontTools.ttLib import TTFont
    return set(TTFont(io.BytesIO(font_bytes)).getBestCmap().keys())


def build(args):
    manifest = load_manifest(args.manifest)
    try:
        import fontTools
    except ImportError:
        fail("fontTools is not installed; see the usage at the top of this script")
    if fontTools.version != manifest["fontTools"]["version"]:
        fail(f"fontTools {fontTools.version} is installed, the manifest pins {manifest['fontTools']['version']}")

    out_dir = args.out
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    os.makedirs(out_dir)
    slice_size = int(manifest["cjkSliceSize"])

    faces = []
    for font in manifest["fonts"]:
        fid = font["id"]
        if not SAFE_NAME.match(fid):
            fail(f"unexpected font id {fid}")
        archive = zipfile.ZipFile(io.BytesIO(download(font["url"], font["sha256"], args.cache)))
        data = member(archive, font["member"], font["memberSha256"], fid)
        licence = member(archive, font["license"], font["licenseSha256"], fid)
        with open(os.path.join(out_dir, f"OFL-{fid}.txt"), "wb") as f:
            f.write(licence)
        stem = os.path.splitext(os.path.basename(font["member"]))[0]
        if "slice" in font:
            for name, cps in slice_cjk(data, font["slice"], stem, slice_size, out_dir):
                faces.append({"file": name, "family": font["family"], "group": fid, "ranges": ranges(cps)})
        else:
            name = os.path.basename(font["member"])
            if not SAFE_NAME.match(name):
                fail(f"unexpected file name {name}")
            with open(os.path.join(out_dir, name), "wb") as f:
                f.write(data)
            faces.append({"file": name, "family": font["family"], "group": fid, "ranges": ranges(coverage(data))})

    index = {"version": 1, "faces": faces}
    with open(os.path.join(out_dir, "index.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(index, f, separators=(",", ":"))

    rows = []
    for face in faces:
        size = os.path.getsize(os.path.join(out_dir, face["file"]))
        chars = char_count(face["ranges"])
        rows.append((face["file"], face["family"], chars, size))
    total = sum(r[3] for r in rows)
    lines = ["| File | Family | Characters | Size (KB) |", "|---|---|---:|---:|"]
    lines += [f"| {r[0]} | {r[1]} | {r[2]} | {r[3] // 1024} |" for r in rows]
    lines.append(f"| index.json | | | {os.path.getsize(os.path.join(out_dir, 'index.json')) // 1024} |")
    lines.append(f"| **Total ({len(rows)} font files)** | | | **{total // 1024}** |")
    report = "\n".join(lines) + "\n"
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as f:
            f.write("### Noto fonts\n\n" + report + "\n")
    print(report)


def requirements(args):
    tool = load_manifest(args.manifest)["fontTools"]
    host = urllib.parse.urlparse(tool["url"]).hostname
    if host not in ALLOWED_HOSTS:
        fail(f"{tool['url']}: unexpected host")
    print(f"fonttools @ {tool['url']} --hash=sha256:{tool['sha256']}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifest", default=MANIFEST)
    commands = parser.add_subparsers(dest="command", required=True)
    b = commands.add_parser("build", help="download, verify, slice and write the fonts")
    b.add_argument("--cache", required=True, help="directory for the downloaded archives (kept between runs)")
    b.add_argument("--out", required=True, help="output directory, replaced: <site>/fonts/noto")
    b.add_argument("--summary", help="append a Markdown size table to this file (CI step summary)")
    b.set_defaults(func=build)
    r = commands.add_parser("requirements", help="print the pip requirement line for the pinned fontTools")
    r.set_defaults(func=requirements)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
