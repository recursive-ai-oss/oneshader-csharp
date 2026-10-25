"""Launches Chromium with WebGPU on the Vulkan backend (hardware adapter, no software fallback)."""
import os

ARGS = [
    "--enable-unsafe-webgpu",
    "--enable-features=Vulkan",
    "--use-angle=vulkan",
    "--ignore-gpu-blocklist",
    "--no-sandbox",
]


def launch(playwright):
    only = os.environ.get("ONESHADER_BROWSER_ARGS_ONLY")
    if only is not None:
        return playwright.chromium.launch(headless=True, channel="chromium", args=only.split())
    extra = os.environ.get("ONESHADER_BROWSER_ARGS", "").split()
    return playwright.chromium.launch(headless=True, channel="chromium", args=ARGS + extra)
