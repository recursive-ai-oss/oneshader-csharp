"""Prints the WebGPU adapter a headless Chromium gets. Run with the venv from tools/run-webgpu-parity.sh."""
import http.server
import json
import sys
import threading

from playwright.sync_api import sync_playwright

import browser

# WebGPU only exists in secure contexts, and http://localhost counts as one.
server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), http.server.SimpleHTTPRequestHandler)
threading.Thread(target=server.serve_forever, daemon=True).start()

with sync_playwright() as p:
    b = browser.launch(p)
    page = b.new_page()
    page.goto(f"http://localhost:{server.server_port}/probe.py")
    info = page.evaluate(
        """async () => {
          if (!navigator.gpu) return {error: 'navigator.gpu missing'};
          const a = await navigator.gpu.requestAdapter();
          if (!a) return {error: 'no adapter'};
          const i = a.info;
          return {vendor: i.vendor, architecture: i.architecture, device: i.device, description: i.description,
                  isFallback: a.isFallbackAdapter ?? i.isFallbackAdapter, features: [...a.features].sort()};
        }"""
    )
    print(json.dumps(info, indent=1))
    print(b.version)
    b.close()
    sys.exit(1 if "error" in info else 0)
