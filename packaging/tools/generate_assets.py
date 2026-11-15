#!/usr/bin/env python3
"""Generate the application and MSIX branding assets."""
import runpy
from pathlib import Path

runpy.run_path(str(Path(__file__).with_name("generate_brand_assets.py")), run_name="__main__")
