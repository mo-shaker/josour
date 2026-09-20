"""Keep legal texts identical across independent Docker/Python build contexts."""

import argparse
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sync", action="store_true", help="copy the root legal texts into each package")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    stale = []
    for package in ("backend", "relay"):
        for name in ("LICENSE", "NOTICE"):
            expected = (root / name).read_bytes()
            target = root / package / name
            if args.sync:
                target.write_bytes(expected)
            if not target.exists() or target.read_bytes() != expected:
                stale.append(str(target.relative_to(root)))
    if stale:
        parser.exit(1, "Out-of-date legal files: " + ", ".join(stale) +
                    "\nRun python3 scripts/check-license-files.py --sync\n")
    print("Package LICENSE and NOTICE copies match the root files.")


if __name__ == "__main__":
    main()
