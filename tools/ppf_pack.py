#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only
"""Builds a content pack from a PPF patch and the disc image it was made for.

    python tools/ppf_pack.py --image DISC.bin --ppf PATCH.ppf --id ID --name NAME --out PACK.zip

The patch is applied in memory; the image is only read. Every file the patch changes goes into the pack whole, keyed
``disc/<path>`` (Form 1 files as their data, streamed files such as MOV/TITLE.STR as raw 2352-byte sectors, as the
installation keeps them). The game reads a pack's files in place of the installed ones while the pack is enabled.

The patch must match the image: a PPF 2.0/3.0 validation block is compared with the image, and a patch is refused
for any other disc. The pack holds files of your own disc; keep it to yourself, never commit or share it.
"""
import argparse
import bisect
import hashlib
import json
import struct
import sys
import zipfile

RAW = 2352


def records(patch: bytes):
    """Yields (image offset, bytes) for each PPF 1.0/2.0/3.0 record, and returns the validation block's check."""
    magic = patch[:5]
    if magic == b"PPF10":
        at, wide, check = 56, False, None
    elif magic == b"PPF20":
        size = struct.unpack_from("<I", patch, 56)[0]
        at, wide, check = 1084, False, (0x9320, patch[60:1084], size)
    elif magic == b"PPF30":
        image_type, has_block, undo = patch[56], patch[57], patch[59]
        at, wide = 60, True
        check = (0x9320 if image_type == 0 else 0x80A0, patch[60:1084], None) if has_block else None
        if has_block:
            at = 1084
    else:
        raise SystemExit("not a PPF patch")
    if magic == b"PPF30" and undo:
        raise SystemExit("PPF 3.0 undo data is not supported")
    out = []
    # A trailing FILE_ID.DIZ ends the records ("@BEGIN_FILE_ID.DIZ").
    end = patch.find(b"@BEGIN_FILE_ID.DIZ")
    end = len(patch) if end < 0 else end
    while at < end:
        if wide:
            offset, length = struct.unpack_from("<QB", patch, at); at += 9
        else:
            offset, length = struct.unpack_from("<IB", patch, at); at += 5
        out.append((offset, patch[at:at + length])); at += length
    return out, check


def files(image, read):
    """The image's ISO 9660 files: (first sector, size, path)."""
    found = []

    def walk(sector, size, prefix):
        data = b"".join(read(sector + k)[24:24 + 2048] for k in range((size + 2047) // 2048))
        p = 0
        while p < len(data):
            length = data[p]
            if length == 0:
                p = (p // 2048 + 1) * 2048
                continue
            entry = data[p:p + length]; p += length
            start, extent = struct.unpack_from("<I", entry, 2)[0], struct.unpack_from("<I", entry, 10)[0]
            name = entry[33:33 + entry[32]]
            if name in (b"\0", b"\1"):
                continue
            name = name.decode("ascii").split(";")[0]
            (walk(start, extent, prefix + name + "/") if entry[25] & 2 else found.append((start, extent, prefix + name)))

    root = read(16)[24 + 156:24 + 190]
    walk(struct.unpack_from("<I", root, 2)[0], struct.unpack_from("<I", root, 10)[0], "")
    return sorted(found)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--image", required=True, help="the disc image the patch was made for (raw 2352-byte BIN)")
    parser.add_argument("--ppf", required=True)
    parser.add_argument("--id", required=True, help="pack ID: lower case letters, digits, . _ -")
    parser.add_argument("--name", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    patch = open(args.ppf, "rb").read()
    changes, check = records(patch)
    with open(args.image, "rb") as image:
        image.seek(0, 2)
        image_size = image.tell()
        if image_size % RAW:
            raise SystemExit("the image is not raw 2352-byte sectors")

        def at(offset, length):
            image.seek(offset)
            return image.read(length)

        if check:
            where, block, size = check
            if (size is not None and size != image_size) or at(where, 1024) != block:
                raise SystemExit("this patch was not made for this disc image (its validation block differs)")

        def read(sector):
            return at(sector * RAW, RAW)

        listing = files(image, read)
        touched = {}
        starts = [f[0] for f in listing]
        for offset, data in changes:
            k = bisect.bisect_right(starts, offset // RAW) - 1
            if k < 0:
                continue
            start, size, path = listing[k]
            stream = path.upper().endswith((".STR", ".XA"))
            sectors = size // 2048 if stream else (size + 2047) // 2048
            # A stream may grow past its listed size (a re-encoded movie); any other file may not.
            if not stream and offset // RAW >= start + sectors:
                raise SystemExit(f"the patch writes past {path} (sector {offset // RAW}); not a whole-file patch")
            touched.setdefault(k, []).append((offset, data))

        assets = {}
        with zipfile.ZipFile(args.out, "w", zipfile.ZIP_DEFLATED) as pack:
            for k, writes in sorted(touched.items()):
                start, size, path = listing[k]
                stream = path.upper().endswith((".STR", ".XA"))
                last = max(o + len(d) for o, d in writes)
                sectors = size // 2048 if stream else (size + 2047) // 2048
                if stream:
                    sectors = max(sectors, -(-(last - start * RAW) // RAW))
                raw = bytearray(at(start * RAW, sectors * RAW))
                for offset, data in writes:
                    raw[offset - start * RAW:offset - start * RAW + len(data)] = data
                body = bytes(raw) if stream else b"".join(raw[s * RAW + 24:s * RAW + 2072] for s in range(sectors))[:size]
                pack.writestr(path, body)
                assets["disc/" + path] = {"file": path, "sha256": hashlib.sha256(body).hexdigest()}
            manifest = {"schema": 1, "id": args.id, "name": args.name, "assets": assets}
            pack.writestr("pack.json", json.dumps(manifest, indent=2) + "\n")
    print(f"{args.out}: {len(assets)} files")


if __name__ == "__main__":
    sys.exit(main())
