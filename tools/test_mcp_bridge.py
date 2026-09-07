"""Regression test for the real MCP SDK stdio endpoint and named-pipe bridge.

Run with a test harness, never a live GUI. No AI client configuration is read/written.
python tools/test_mcp_bridge.py --dotnet <dotnet.exe> --mcp <Mcp.dll-or-exe>
    --harness <McpBridgeHarness.dll>
"""

import argparse
import json
import os
import queue
import subprocess
import threading
import time
import uuid


def command(dotnet, target, *extra):
    return ([dotnet, target] if target.lower().endswith(".dll") else [target]) + list(extra)


class Rpc:
    def __init__(self, cmd):
        self.process = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, encoding="utf-8", bufsize=1,
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.output = queue.Queue()
        self.errors = []
        threading.Thread(target=self._stdout, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr), daemon=True).start()
        self.next_id = 0

    def _stdout(self):
        for line in self.process.stdout:
            try:
                self.output.put(json.loads(line))
            except json.JSONDecodeError:
                self.output.put({"invalid_stdout": line})

    def send(self, method, params=None, notification=False):
        self.next_id += 1
        message = {"jsonrpc": "2.0", "method": method}
        if not notification:
            message["id"] = self.next_id
        if params is not None:
            message["params"] = params
        self.process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        if notification:
            return None
        until = time.monotonic() + 10
        while time.monotonic() < until:
            reply = self.output.get(timeout=max(.1, until - time.monotonic()))
            assert "invalid_stdout" not in reply, reply
            if reply.get("id") == self.next_id:
                return reply
        raise AssertionError("RPC timeout: " + "".join(self.errors))

    def tool(self, name, arguments=None):
        response = self.send("tools/call", {"name": name, "arguments": arguments or {}})
        result = response["result"]
        text = json.loads(result["content"][0]["text"])
        assert result.get("isError", False) == (not text["accepted"]), result
        return text

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--mcp", required=True)
    parser.add_argument("--harness", required=True)
    args = parser.parse_args()
    # Both child processes inherit this private channel. A live user pet keeps
    # its normal channel, and the harness marker is still checked before writes.
    os.environ["EAGLE_PET_TEST_CHANNEL"] = uuid.uuid4().hex
    harness = subprocess.Popen(command(args.dotnet, args.harness), stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding="utf-8",
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    rpc = None
    try:
        assert harness.stdout.readline().strip() == "READY", "Harness failed to start"
        rpc = Rpc(command(args.dotnet, args.mcp, "--source", "MCP 测试"))
        init = rpc.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                       "clientInfo": {"name": "eagle-regression-test", "version": "1"}})
        assert init["result"]["protocolVersion"] == "2025-11-25", init
        rpc.send("notifications/initialized", notification=True)
        names = {t["name"] for t in rpc.send("tools/list")["result"]["tools"]}
        assert names == {"pet_notify", "pet_get_state"}, names
        state = rpc.tool("pet_get_state")
        assert (state.get("state") or {}).get("testHarness") is True, "Refusing to send test events to a real desktop pet"
        params = {"eventId": "test-1", "sessionId": "session-A", "eventType": "reply_ready", "message": "完成啦"}
        assert rpc.tool("pet_notify", params)["status"] == "accepted"
        assert rpc.tool("pet_notify", params)["status"] == "duplicate"
        assert rpc.tool("pet_notify", dict(params, eventId="test-2"))["status"] == "rate_limited"
        assert rpc.tool("pet_notify", dict(params, source="forged"))["status"] == "invalid_request"
        assert rpc.tool("pet_notify", dict(params, eventType="execute"))["status"] == "invalid_request"
        assert rpc.tool("pet_notify", dict(params, message="x" * 241))["status"] == "invalid_request"
        assert rpc.tool("pet_notify", dict(params, message="bad\u202esource"))["status"] == "invalid_request"
        assert rpc.tool("pet_notify", dict(params, sessionId="\n"))["status"] == "invalid_request"
        assert rpc.tool("pet_get_state", {"source": "forged"})["status"] == "invalid_request"
        final = rpc.tool("pet_get_state")["state"]
        assert final["count"] == 1 and final["events"][0]["source"] == "MCP 测试", final
        time.sleep(3.1)
        assert rpc.tool("pet_notify", dict(params, eventId="test-2", eventType="needs_attention"))["status"] == "accepted"
        assert rpc.tool("pet_get_state")["state"]["count"] == 2
        time.sleep(3.1)
        one_shot = subprocess.run(command(args.dotnet, args.mcp, "--source", "Hook 测试", "--notify",
                                           "--event-id", "hook-1", "--event-type", "reply_ready",
                                           "--message", "测试通知"), capture_output=True, encoding="utf-8", timeout=10,
                                  creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        assert one_shot.returncode == 0 and json.loads(one_shot.stdout)["status"] == "accepted", one_shot
        assert rpc.tool("pet_get_state")["state"]["events"][-1]["source"] == "Hook 测试"
        print("PASS: initialize, tools/list, tools/call, source binding, dedup, rate limit, validation, Unicode safety, pipe delivery")
        print("PASS: packet size bounds, serialization round-trip, standalone hook notification mode")
        harness.stdin.write("stop\n")
        harness.stdin.flush()
        harness.wait(timeout=5)
        assert rpc.tool("pet_get_state")["status"] == "unavailable"
        print("PASS: pet-offline is a tool error, not fake delivery")
    finally:
        if rpc:
            rpc.close()
        if harness.poll() is None:
            harness.kill()
            harness.wait()


if __name__ == "__main__":
    main()
