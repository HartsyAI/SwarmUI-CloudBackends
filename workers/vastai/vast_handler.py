"""Vast.ai PyWorker Handler for SwarmUI Serverless Worker.

Mirrors the RunPod handler (rp_handler.py) but adapted for Vast.ai's serverless framework.
Copy this file to your Vast.ai worker Docker image repo.

Workflow:
1. Container starts: start.sh launches SwarmUI, this script runs alongside
2. This handler waits for SwarmUI to become ready on localhost:7801
3. Client calls Vast.ai /route/ to get this worker's URL
4. Client sends wakeup action -> handler returns public_url, session_id, worker_id
5. Client makes direct SwarmUI API calls to the public_url
6. Background keepalive pings from the C# extension keep the worker alive

Action Design:
- wakeup:    Returns immediately with public_url, session_id, worker_id. Non-blocking.
- keepalive: Blocking ping loop for 'duration' seconds. Keeps the worker alive.
- ready:     Quick check - returns connection info if SwarmUI is up.
- health:    Lightweight HTTP GET health check.
- shutdown:  Acknowledges shutdown signal.

Session Management:
- Session created ONCE at startup and cached globally
- All actions return the same cached session
- If session expires, we recreate it automatically
"""

import os
import sys
import time
import traceback
import requests

from typing import Dict, Any, Optional
from flask import Flask, request as flask_request, jsonify

SWARMUI_API_URL = os.getenv('SWARMUI_API_URL', 'http://127.0.0.1:7801')
SWARMUI_PORT = os.getenv('SWARMUI_PORT', '7801')
STARTUP_TIMEOUT = int(os.getenv('STARTUP_TIMEOUT', '1800'))
CHECK_INTERVAL = 10

# Vast.ai environment variables for constructing the public URL
PUBLIC_IPADDR = os.getenv('PUBLIC_IPADDR', 'localhost')
# Vast.ai maps container ports to external ports via VAST_TCP_PORT_{internal_port}
VAST_TCP_PORT = os.getenv(f'VAST_TCP_PORT_{SWARMUI_PORT}', SWARMUI_PORT)
VAST_CONTAINERLABEL = os.getenv('VAST_CONTAINERLABEL', 'unknown')

# Global session cache
CACHED_SESSION_ID: Optional[str] = None
CACHED_VERSION: Optional[str] = None

session = requests.Session()
adapter = requests.adapters.HTTPAdapter(
    max_retries=requests.adapters.Retry(
        total=5,
        backoff_factor=0.3,
        status_forcelist=[500, 502, 503, 504],
        allowed_methods=["GET", "POST"]
    )
)
session.mount('http://', adapter)
session.mount('https://', adapter)
session.headers.update({
    'User-Agent': 'SwarmUI-VastAi-Worker/1.0',
    'Content-Type': 'application/json'
})

app = Flask(__name__)


class Log:
    @staticmethod
    def header(msg: str) -> None:
        print("\n" + "=" * 80)
        print(msg)
        print("=" * 80 + "\n")

    @staticmethod
    def info(msg: str) -> None:
        print(f"[INFO] {msg}")

    @staticmethod
    def verbose(msg: str) -> None:
        print(f"[VERBOSE] {msg}")

    @staticmethod
    def success(msg: str) -> None:
        print(f"[SUCCESS] {msg}")

    @staticmethod
    def error(msg: str) -> None:
        print(f"[ERROR] {msg}", file=sys.stderr)

    @staticmethod
    def warning(msg: str) -> None:
        print(f"[WARNING] {msg}")


def get_public_url() -> str:
    """Construct the public URL where SwarmUI is directly accessible.

    On Vast.ai, container ports are mapped to external ports.
    The external port is provided via VAST_TCP_PORT_{internal_port} env var.
    """
    url = f"http://{PUBLIC_IPADDR}:{VAST_TCP_PORT}"
    Log.verbose(f"Public URL: {url}")
    return url


def swarm_request(method: str, path: str, payload: Optional[Dict[str, Any]] = None,
                  timeout: int = 30) -> Dict[str, Any]:
    url = f"{SWARMUI_API_URL.rstrip('/')}/{path.lstrip('/')}"
    Log.verbose(f"SwarmUI request: {method} {url} (timeout: {timeout}s)")
    try:
        if method.upper() == 'GET':
            response = session.get(url, timeout=timeout)
        elif method.upper() == 'POST':
            response = session.post(url, json=payload or {}, timeout=timeout)
        else:
            raise ValueError(f"Unsupported method: {method}")
        Log.verbose(f"SwarmUI response: {response.status_code} ({len(response.content)} bytes)")
        response.raise_for_status()
        return response.json() if response.content else {}
    except Exception as e:
        Log.verbose(f"SwarmUI request failed: {method} {url} -> {e}")
        raise RuntimeError(f"SwarmUI request failed: {e}")


def get_or_create_session() -> tuple[str, str]:
    global CACHED_SESSION_ID, CACHED_VERSION
    if CACHED_SESSION_ID:
        Log.verbose(f"Using cached session: {CACHED_SESSION_ID[:16]}...")
        return CACHED_SESSION_ID, CACHED_VERSION
    Log.info("Creating new SwarmUI session...")
    max_retries = 3
    for attempt in range(max_retries):
        try:
            session_info = swarm_request('POST', '/API/GetNewSession', timeout=10)
            CACHED_SESSION_ID = session_info.get('session_id')
            CACHED_VERSION = session_info.get('version', 'unknown')
            if not CACHED_SESSION_ID:
                raise RuntimeError("Failed to get session ID from SwarmUI")
            Log.success(f"Session created: {CACHED_SESSION_ID[:16]}...")
            return CACHED_SESSION_ID, CACHED_VERSION
        except Exception as e:
            if attempt < max_retries - 1:
                Log.warning(f"Session creation attempt {attempt + 1} failed: {e}, retrying...")
                time.sleep(2)
            else:
                raise RuntimeError(f"Failed to create session: {e}")


def keepalive_ping() -> bool:
    try:
        url = f"{SWARMUI_API_URL.rstrip('/')}"
        response = session.get(url, timeout=5, allow_redirects=False)
        alive = response.status_code in [200, 301, 302, 303, 307, 308]
        Log.verbose(f"Keepalive ping: {response.status_code} (alive={alive})")
        return alive
    except Exception as e:
        Log.warning(f"Keepalive ping failed: {e}")
        return False


def wait_for_swarmui_ready(max_wait_seconds: int = STARTUP_TIMEOUT) -> bool:
    global CACHED_SESSION_ID, CACHED_VERSION
    Log.header("Waiting for SwarmUI to be ready")
    Log.info(f"URL: {SWARMUI_API_URL}")
    Log.info(f"Public URL: {get_public_url()}")
    Log.info(f"Max wait: {max_wait_seconds}s")
    start_time = time.time()
    max_attempts = max(1, max_wait_seconds // CHECK_INTERVAL)
    for attempt in range(max_attempts):
        elapsed = int(time.time() - start_time)
        try:
            session_info = swarm_request('POST', '/API/GetNewSession', timeout=10)
            session_id = session_info.get('session_id')
            if session_id:
                CACHED_SESSION_ID = session_id
                CACHED_VERSION = session_info.get('version', 'unknown')
                Log.success(f"SwarmUI API ready after {elapsed}s")
                Log.info(f"Version: {CACHED_VERSION}")
                Log.info(f"Session: {CACHED_SESSION_ID[:16]}...")
                return True
            else:
                Log.info(f"[{elapsed:4d}s] Waiting for valid session...")
        except Exception as e:
            Log.info(f"[{elapsed:4d}s] Connecting: {e}")
        if elapsed >= max_wait_seconds:
            break
        time.sleep(CHECK_INTERVAL)
    Log.error(f"SwarmUI not ready after {max_wait_seconds}s")
    return False


# ---- Action Handlers ----


def action_wakeup(job_input: Dict[str, Any]) -> Dict[str, Any]:
    Log.info("Action: wakeup (non-blocking)")
    try:
        session_id, version = get_or_create_session()
        public_url = get_public_url()
        Log.info(f"Worker ready at {public_url}")
        return {
            'success': True,
            'public_url': public_url,
            'session_id': session_id,
            'version': version,
            'worker_id': VAST_CONTAINERLABEL
        }
    except Exception as e:
        Log.error(f"Wakeup failed: {e}")
        Log.verbose(traceback.format_exc())
        return {'success': False, 'error': str(e)}


def action_ready(job_input: Dict[str, Any]) -> Dict[str, Any]:
    Log.info("Action: ready")
    try:
        session_id, version = get_or_create_session()
        return {
            'ready': True,
            'public_url': get_public_url(),
            'session_id': session_id,
            'version': version,
            'worker_id': VAST_CONTAINERLABEL
        }
    except Exception as e:
        return {'ready': False, 'error': str(e)}


def action_health(job_input: Dict[str, Any]) -> Dict[str, Any]:
    Log.info("Action: health")
    try:
        url = f"{SWARMUI_API_URL.rstrip('/')}"
        response = session.get(url, timeout=5, allow_redirects=False)
        healthy = response.status_code in [200, 301, 302, 303, 307, 308]
        return {
            'healthy': healthy,
            'public_url': get_public_url(),
            'worker_id': VAST_CONTAINERLABEL
        }
    except Exception as e:
        return {'healthy': False, 'error': str(e)}


def action_keepalive(job_input: Dict[str, Any]) -> Dict[str, Any]:
    """Blocking ping loop. Keeps the worker alive for 'duration' seconds."""
    duration = int(job_input.get('duration', 3600))
    interval = int(job_input.get('interval', 30))
    if duration <= 0:
        return {'success': False, 'error': 'duration must be positive'}
    interval = max(1, interval)
    Log.info(f"Action: keepalive (blocking for {duration}s, ping every {interval}s)")
    pings = 0
    failures = 0
    end_time = time.time() + duration
    while time.time() < end_time:
        if keepalive_ping():
            pings += 1
        else:
            failures += 1
        if (pings + failures) % 10 == 0:
            remaining = int(end_time - time.time())
            Log.verbose(f"Keepalive: {pings} ok, {failures} failed, {remaining}s remaining")
        time.sleep(interval)
    Log.info(f"Keepalive complete: {pings} pings, {failures} failures over {duration}s")
    return {
        'success': True,
        'public_url': get_public_url(),
        'worker_id': VAST_CONTAINERLABEL,
        'pings': pings,
        'failures': failures,
        'duration': duration,
        'interval': interval
    }


def action_shutdown(job_input: Dict[str, Any]) -> Dict[str, Any]:
    Log.warning("Action: shutdown signal received")
    return {
        'success': True,
        'message': 'Shutdown acknowledged',
        'worker_id': VAST_CONTAINERLABEL
    }


ACTIONS = {
    'wakeup': action_wakeup,
    'ready': action_ready,
    'health': action_health,
    'keepalive': action_keepalive,
    'shutdown': action_shutdown,
}


# ---- Flask HTTP Handler (receives routed requests from Vast.ai) ----


@app.route('/handler', methods=['POST'])
def handler():
    """Main handler route. Vast.ai's routing system sends requests here.

    The PyWorker framework validates auth_data before this is called.
    We receive the payload directly (auth_data already stripped by the framework).
    If running standalone (without PyWorker framework), the full envelope arrives
    and we extract the payload ourselves.
    """
    try:
        data = flask_request.get_json(force=True)
        # Support both direct payload and envelope format
        if 'payload' in data:
            payload = data['payload']
        else:
            payload = data
        action = payload.get('action', 'wakeup')
        Log.info(f"Handler invoked: action={action}")
        if action in ACTIONS:
            result = ACTIONS[action](payload)
            return jsonify(result)
        else:
            return jsonify({
                'success': False,
                'error': f'Unknown action: {action}',
                'available_actions': list(ACTIONS.keys())
            })
    except Exception as e:
        Log.error(f"Handler error: {e}")
        Log.verbose(traceback.format_exc())
        return jsonify({
            'success': False,
            'error': str(e),
            'traceback': traceback.format_exc()
        }), 500


@app.route('/health', methods=['GET'])
def health_check():
    """Simple health check endpoint for Vast.ai monitoring."""
    try:
        _, _ = get_or_create_session()
        return jsonify({'status': 'healthy'}), 200
    except Exception:
        return jsonify({'status': 'starting'}), 503


# ---- Main Entry Point ----


if __name__ == "__main__":
    Log.header("SwarmUI Vast.ai Serverless Worker")

    if not wait_for_swarmui_ready():
        Log.error("SwarmUI failed to start")
        sys.exit(1)

    Log.header("System Ready - Starting HTTP Handler")
    Log.info(f"Public URL: {get_public_url()}")
    Log.info(f"Worker ID: {VAST_CONTAINERLABEL}")
    Log.info(f"Cached Session: {CACHED_SESSION_ID[:16]}...")

    # Start Flask server on port 8000 (the port Vast.ai routes to)
    HANDLER_PORT = int(os.getenv('HANDLER_PORT', '8000'))
    Log.info(f"Handler listening on port {HANDLER_PORT}")
    app.run(host='0.0.0.0', port=HANDLER_PORT, threaded=True)
