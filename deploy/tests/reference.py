import base64
import concurrent.futures
import hashlib
import hmac
import json
import os
import ssl
import struct
import sys
import time
import statistics
import urllib.error
import urllib.request
from pathlib import Path

origin = os.environ["PUBLIC_ORIGIN"]
tls = ssl.create_default_context(cafile=str(Path(os.environ["TLS_DIRECTORY"]) / "cert.pem"))
password = os.environ["REHEARSAL_PASSWORD"]
email = "operator@example.test"
evidence = Path(os.environ["EVIDENCE_DIRECTORY"])


def request(path, payload=None, token=None, method=None, extra=None):
    headers = extra.copy() if extra else {}
    if token:
        headers["Authorization"] = "Bearer " + token
    if payload is not None:
        headers["Content-Type"] = "application/json"
    body = json.dumps(payload).encode() if payload is not None else None
    query = urllib.request.Request(origin + path, body, headers, method=method)
    try:
        with urllib.request.urlopen(query, context=tls, timeout=8) as response:
            data = response.read()
            return response.status, json.loads(data) if data.startswith((b"{", b"[")) else None
    except urllib.error.HTTPError as response:
        return response.code, None


def login(code=None):
    status, tokens = request("/api/v1/auth/login", {"email": email, "password": password, "twoFactorCode": code})
    assert status == 200, f"Login did not succeed ({status})"
    return tokens


def otp(key):
    decoded = base64.b32decode(key.upper() + "=" * (-len(key) % 8))
    digest = hmac.new(decoded, struct.pack(">Q", int(time.time()) // 30), hashlib.sha1).digest()
    offset = digest[-1] & 15
    return f"{int.from_bytes(digest[offset:offset + 4], 'big') % (2 ** 31) % 1000000:06d}"


if sys.argv[1] == "enroll":
    assert request("/health/ready")[0] == 200
    credentials = login()
    assert request("/api/v1/admin/users", token=credentials["accessToken"])[0] == 403
    step_up = {"X-Current-Password": password}
    status, setup = request("/api/v1/auth/manage/2fa", {}, credentials["accessToken"], extra=step_up)
    assert status == 200
    key = setup["sharedKey"]
    credentials = login()
    assert request("/api/v1/auth/manage/2fa", {"enable": True, "twoFactorCode": otp(key)},
                   credentials["accessToken"], extra=step_up)[0] == 200
    credentials = login(otp(key))
    assert request("/api/v1/admin/users", token=credentials["accessToken"])[0] == 200
    status, note = request("/api/v1/notes", {"title": "Recovery indicator", "body": ""},
                           credentials["accessToken"], extra={"Idempotency-Key": "rehearsal-indicator"})
    assert status == 201
    (evidence / "private-session.json").write_text(json.dumps(
        {"token": credentials["accessToken"], "key": key, "note": note["id"]}))
    print("Production HTTPS, operator MFA, and transactional note creation verified.")
elif sys.argv[1] == "replica":
    credentials = json.loads((evidence / "private-session.json").read_text())
    assert request("/api/v1/me", token=credentials["token"])[0] == 200
    assert request("/api/v1/notes/" + credentials["note"], token=credentials["token"])[0] == 200
    print("The existing session works after replacing the replicas.")
elif sys.argv[1] == "workload":
    credentials = json.loads((evidence / "private-session.json").read_text())
    token = credentials["token"]
    notes = []
    timings = []
    sample_count = int(os.getenv("REHEARSAL_NOTE_SAMPLES", "12"))
    rate = float(os.getenv("REHEARSAL_NOTE_RATE", "0"))
    assert 2 <= sample_count <= 200 and 0 <= rate <= 15
    started_load = time.monotonic()
    for number in range(sample_count):
        started = time.monotonic()
        status, note = request("/api/v1/notes", {"title": f"Capacity fixture {number}", "body": ""}, token,
                               extra={"Idempotency-Key": f"capacity-{number}"})
        assert status == 201, status
        notes.append(note["id"])
        timings.append(time.monotonic() - started)
        if rate:
            time.sleep(max(0, started_load + (number + 1) / rate - time.monotonic()))
    producer_seconds = time.monotonic() - started_load
    (evidence / "jobs.json").write_text(json.dumps(notes))
    p95 = statistics.quantiles(timings, n=100, method="inclusive")[94]
    budget = os.getenv("MAX_P95_SECONDS")
    assert not budget or p95 <= float(budget), p95

    def burst(index):
        return request("/health/live", extra={"X-Forwarded-For": f"192.0.2.{index + 1}"})[0]

    with concurrent.futures.ThreadPoolExecutor(max_workers=20) as workers:
        results = list(workers.map(burst, range(100)))
    assert 200 in results and 429 in results, results
    result = {"noteSamples": len(notes), "p95NoteSeconds": p95, "noteProducerSeconds": producer_seconds, "sharedIpAccepted": results.count(200),
              "sharedIpThrottled": results.count(429)}
    (evidence / "workload.json").write_text(json.dumps(result))
    print(json.dumps(result))
elif sys.argv[1] == "pool":
    credentials = json.loads((evidence / "private-session.json").read_text())

    def locked_write(number):
        return request("/api/v1/notes", {"title": "Pool fixture", "body": ""}, credentials["token"],
                       extra={"Idempotency-Key": f"pool-{number}"})[0]

    with concurrent.futures.ThreadPoolExecutor(max_workers=30) as workers:
        results = list(workers.map(locked_write, range(30)))
    assert results == [201] * 30, results
else:
    raise ValueError("Unknown rehearsal step.")
