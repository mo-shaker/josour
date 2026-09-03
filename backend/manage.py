#!/usr/bin/env python3
"""Shim so ``python manage.py <command>`` runs the Typer CLI in app/cli.py."""

from app.cli import main

if __name__ == "__main__":
    main()
