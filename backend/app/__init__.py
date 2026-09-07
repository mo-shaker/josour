"""Josour control server.

The version is read from the installed package metadata so it cannot drift from ``pyproject.toml``.
"""

from importlib.metadata import PackageNotFoundError
from importlib.metadata import version as _version

try:
    __version__ = _version("josour-backend")
except PackageNotFoundError:  # running from a source tree that was never installed
    __version__ = "0.0.0+dev"

__all__ = ["__version__"]
