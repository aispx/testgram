#!/usr/bin/env python3
"""Download all Telegram language packs for every supported platform."""

from __future__ import annotations

import sys

from import_telegram_langpacks import main


if __name__ == "__main__":
    sys.argv = [
        sys.argv[0],
        "--all-platforms",
        "--all-languages",
        *sys.argv[1:],
    ]
    raise SystemExit(main())
