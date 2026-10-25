"""Runs parity jobs in headless Chromium on WebGPU.

    python run.py JOBS_DIR RESULTS_DIR

Every JOBS_DIR/*.json is one job (see tests/OneShader.ParityTests/WebGpuParityTests.cs); its outputs and any
WebGPU errors go to RESULTS_DIR/<name>.json. RESULTS_DIR/adapter.json records the adapter the browser used.
The run fails (exit 2) if the browser only offers a software adapter, since the point is real-GPU behaviour;
ONESHADER_ALLOW_SOFTWARE_ADAPTER=1 allows it for debugging the tooling without touching the GPU.
"""
import functools
import http.server
import json
import os
import sys
import threading

from playwright.sync_api import sync_playwright

import browser

HERE = os.path.dirname(os.path.abspath(__file__))


class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def main(jobs_dir, results_dir):
    os.makedirs(results_dir, exist_ok=True)
    handler = functools.partial(Quiet, directory=HERE)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f"http://localhost:{server.server_port}/index.html"

    with sync_playwright() as p:
        b = browser.launch(p)
        console = []

        def new_page():
            pg = b.new_page()
            pg.on("console", lambda m: console.append(f"{m.type}: {m.text}"))
            pg.on("pageerror", lambda e: console.append(f"pageerror: {e}"))
            pg.goto(url)
            return pg

        page = new_page()
        info = page.evaluate("window.adapterInfo()")
        info["browser"] = b.version
        with open(os.path.join(results_dir, "adapter.json"), "w") as f:
            json.dump(info, f, indent=1)
        print("adapter:", json.dumps({k: info[k] for k in ("vendor", "architecture", "isFallbackAdapter", "browser")}))
        if info["isFallbackAdapter"] and os.environ.get("ONESHADER_ALLOW_SOFTWARE_ADAPTER") != "1":
            print("the browser only offers a software adapter", file=sys.stderr)
            return 2

        names = sorted(n for n in os.listdir(jobs_dir) if n.endswith(".json"))
        for n in names:
            with open(os.path.join(jobs_dir, n)) as f:
                job = json.load(f)
            del console[:]
            try:
                result = page.evaluate("job => window.runJob(job)", job)
            except Exception as e:  # a crashed page or lost device: record and carry on with a fresh page
                result = {"name": job["name"], "outputs": [], "errors": [f"browser: {e}"]}
                try:
                    page.close()
                except Exception:
                    pass
                page = new_page()
            result["console"] = [c for c in console if not c.startswith("log:")]
            with open(os.path.join(results_dir, n), "w") as f:
                json.dump(result, f)
            print(f"{job['name']}: {len(result['outputs'])} outputs, {len(result['errors'])} errors", flush=True)
        b.close()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
