import json
import logging
import math
import os
from pathlib import Path
import queue
import re
import ssl
import time
import uuid
from .analytics import analyze, correlate
from .scenarios import run_scenario
from .store import Store, ID

LOG = logging.getLogger("guardian")
CLIENT = re.compile(r"^[A-Za-z0-9_-]{1,64}$")


def handle_request(store, topic, payload, now=None):
    now = time.time() if now is None else now
    client = topic.removeprefix("aquatwin/guardian/requests/")
    if not CLIENT.fullmatch(client) or len(payload.encode()) > 8192: raise ValueError("invalid request routing/size")
    request = json.loads(payload)
    if not isinstance(request, dict): raise ValueError("request must be a JSON object")
    request_id = request.get("request_id", "")
    well_id = request.get("well_id", "")
    if not CLIENT.fullmatch(request_id) or not isinstance(well_id, str) or not ID.fullmatch(well_id): raise ValueError("invalid request or well identity")
    sent_at=float(request.get("sent_at", 0))
    if not math.isfinite(sent_at) or abs(now-sent_at) > 300: raise ValueError("expired request; check the browser clock")
    action = request.get("action")
    if action == "scenario":
        inputs=request.get("inputs", {})
        if not isinstance(inputs,dict): raise ValueError("scenario inputs must be an object")
        result = run_scenario(store, client+"_"+request_id, well_id, inputs, now)
    elif action == "scenarios": result = {"scenarios": store.scenarios(well_id)}
    else: raise ValueError("unknown action")
    return f"aquatwin/guardian/replies/{client}", {"request_id": request_id, "well_id": well_id, "action": action, "ok": True, "result": result}


def run(config_path):
    import paho.mqtt.client as mqtt
    config_path = Path(config_path).resolve()
    cfg = json.loads(config_path.read_text(encoding="utf-8-sig"))
    username, password = os.environ.get(cfg.get("username_env", "AQUATWIN_MQTT_USERNAME")), os.environ.get(cfg.get("password_env", "AQUATWIN_MQTT_PASSWORD"))
    if not username or not password: raise ValueError("Set MQTT username/password environment variables; do not put secrets in source files")
    store = Store(os.environ.get("AQUATWIN_DATABASE") or config_path.parent / cfg.get("database", "data/guardian.sqlite3"), cfg)
    inbox = queue.Queue(maxsize=4096)
    transport = cfg.get("transport", "tcp")
    if transport not in ("tcp", "websockets"): raise ValueError("transport must be tcp or websockets")
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="Guardian-"+uuid.uuid4().hex[:16], transport=transport)
    client.username_pw_set(username, password)
    client.tls_set_context(ssl.create_default_context())
    if transport == "websockets": client.ws_set_options(path=cfg.get("websocket_path", "/mqtt"))
    client.reconnect_delay_set(2, 60)
    client.max_queued_messages_set(2048)
    client.will_set("aquatwin/guardian/status", json.dumps({"state": "OFFLINE"}), qos=1, retain=True)

    def connected(c, userdata, flags, reason, properties):
        if reason.is_failure: LOG.error("Broker rejected connection: %s", reason); return
        c.subscribe([(cfg.get("telemetry_topic", "aquatwin/well/+/telemetry"), 1), ("aquatwin/guardian/requests/+", 1)])
        LOG.info("Connected; telemetry and scenario subscriptions requested")

    def subscribed(c, userdata, mid, reason_codes, properties):
        if any(code.is_failure for code in reason_codes): LOG.error("Subscription denied; check backend credential topic permissions")
        else: LOG.info("Subscriptions acknowledged")

    def message(c, userdata, msg):
        if len(msg.payload) > 16384: LOG.warning("Oversized message ignored"); return
        try: inbox.put_nowait((msg.topic, msg.payload.decode("utf-8"), bool(msg.retain)))
        except (queue.Full, UnicodeError): LOG.warning("Input queue full or non-UTF8 message; packet ignored")

    client.on_connect, client.on_message, client.on_subscribe = connected, message, subscribed
    client.on_connect_fail = lambda c,u: LOG.error("Connection failed; check broker hostname, TLS trust, port and network. Retrying with backoff.")
    client.on_disconnect = lambda c,u,f,r,p: LOG.warning("Broker disconnected: %s; automatic reconnect enabled", r)
    client.connect_async(cfg["broker_host"], int(cfg.get("broker_port", 8883)), 60)
    client.loop_start()
    due, rates = 0., {}
    try:
        while True:
            try:
                topic, payload, retained = inbox.get(timeout=.5)
                if topic.startswith("aquatwin/guardian/requests/"):
                    if retained: continue  # commands are never replayed from retained messages
                    key = topic.rsplit("/", 1)[-1]
                    if time.monotonic()-rates.get(key, -100) < 1: continue
                    if len(rates) > 1024: rates.clear()
                    rates[key] = time.monotonic()
                    try:
                        reply, result = handle_request(store, topic, payload)
                    except (ValueError, KeyError, TypeError) as exc:
                        # Route only to a validated client namespace. Never log credentials/payloads.
                        request = json.loads(payload)
                        if not isinstance(request,dict): request={}
                        if not CLIENT.fullmatch(key): continue
                        reply, result = f"aquatwin/guardian/replies/{key}", {"request_id": str(request.get("request_id", ""))[:64], "ok": False, "error": str(exc)}
                    client.publish(reply, json.dumps(result, allow_nan=False), qos=1, retain=False)
                else:
                    info = store.ingest(payload, topic, retained)
                    if info["inserted"]: LOG.info("Stored %s; eligible=%s flags=%s", info["well_id"], info["eligible"], ",".join(info["flags"]))
            except queue.Empty: pass
            except (ValueError, KeyError, TypeError, json.JSONDecodeError): LOG.warning("Rejected invalid telemetry/request")
            now = time.time()
            if now >= due and client.is_connected():
                results = correlate(store, [analyze(store, well, now) for well in store.wells()], now)
                for result in results:
                    store.save_insight(result)
                    client.publish("aquatwin/insights/well/"+result["well_id"], json.dumps(result, allow_nan=False), qos=1, retain=True)
                client.publish("aquatwin/guardian/status", json.dumps({"state": "ONLINE", "computed_at": now, "well_count": len(results)}), qos=1, retain=True)
                due = now + max(5, cfg.get("publish_interval_seconds", 15))
    except KeyboardInterrupt: LOG.info("Stopping")
    finally:
        try:
            if client.is_connected(): client.publish("aquatwin/guardian/status", json.dumps({"state": "OFFLINE"}), qos=1, retain=True).wait_for_publish(timeout=2)
        finally:
            client.disconnect(); client.loop_stop(); store.close()
