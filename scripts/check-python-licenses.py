"""Check legal texts in built wheels and source distributions for both services."""

import sys
import tarfile
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
output = Path(sys.argv[1])
for package in ("backend", "relay"):
    wheels = list((output / package).glob("*.whl"))
    sources = list((output / package).glob("*.tar.gz"))
    assert len(wheels) == len(sources) == 1, f"Expected one wheel and sdist for {package}"
    with zipfile.ZipFile(wheels[0]) as wheel:
        metadata = next(n for n in wheel.namelist() if n.endswith(".dist-info/METADATA"))
        assert "License-Expression: Apache-2.0\n" in wheel.read(metadata).decode()
        for name in ("LICENSE", "NOTICE"):
            path = next(n for n in wheel.namelist() if n.endswith(".dist-info/licenses/" + name))
            assert wheel.read(path) == (root / name).read_bytes(), (package, name)
    with tarfile.open(sources[0]) as source:
        prefix = source.getnames()[0].split("/")[0]
        for name in ("LICENSE", "NOTICE"):
            with source.extractfile(prefix + "/" + name) as stream:
                assert stream.read() == (root / name).read_bytes(), (package, name)
    print(f"{package}: wheel and sdist contain the canonical legal texts and Apache-2.0 metadata")
