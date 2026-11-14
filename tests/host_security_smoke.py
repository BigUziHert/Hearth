"""Independent HTTP boundary checks against a real Hearth.Host process (stdlib only)."""
import hashlib
import io
import json
import pathlib
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
DOTNET = ROOT / "tools/dotnet/dotnet.exe"
HOST = ROOT / "src/Hearth.Host/bin/Release/net10.0/Hearth.Host.dll"
COMMAND = [str(DOTNET) if DOTNET.exists() else "dotnet", str(HOST)]
checks = []

def expect(condition, message):
    assert condition, message
    checks.append(message)
    print("PASS", message)

with tempfile.TemporaryDirectory(prefix="hearth-host-security-") as directory:
    directory = pathlib.Path(directory)
    token = secrets.token_hex(32)
    token_file = directory / "bootstrap.txt"
    token_file.write_text(token)
    data = directory / "host"
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    subprocess.run(COMMAND + ["--init", "--data", str(data), "--owner-token-file", str(token_file), "--port", str(port)], check=True)
    token_file.unlink()
    base = f"http://127.0.0.1:{port}"
    log = (directory / "host.log").open("w")
    process = None

    def call(method, path, credential=None, payload=None, raw=False):
        body = payload if isinstance(payload, bytes) else json.dumps(payload).encode() if payload is not None else None
        request = urllib.request.Request(base + path, data=body, method=method)
        if credential:
            request.add_header("Authorization", "Bearer " + credential)
        if body:
            request.add_header("Content-Type", "application/octet-stream" if isinstance(payload, bytes) else "application/json")
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                content = response.read()
                return response.status, content if raw else json.loads(content) if content else None
        except urllib.error.HTTPError as error:
            content = error.read()
            return error.code, content if raw else json.loads(content) if content else None

    def start():
        global process
        process = subprocess.Popen(COMMAND + ["--data", str(data)], stdout=log, stderr=log)
        for _ in range(100):
            try:
                if call("GET", "/health")[0] == 200:
                    return
            except (OSError, urllib.error.URLError):
                time.sleep(.05)
        raise RuntimeError("Host did not start")

    def jar(text, modid=None):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n")
            archive.writestr("custom.txt", text)
            if modid:
                archive.writestr("fabric.mod.json", json.dumps(dict(schemaVersion=1, id=modid, version="1.0.0")))
        return output.getvalue()

    try:
        start()
        expect(token not in (data / "host.json").read_text(), "Owner credential stored hashed")
        expect(call("GET", "/owner/packs")[0] == 401, "Anonymous owner listing denied")
        packs = []
        for name in ["Private A", "Private B"]:
            code, pack = call("POST", "/owner/packs", token, dict(name=name, minecraftVersion="1.21.1", loader="fabric", loaderVersion="0.16.10"))
            expect(code == 200, name + " created")
            packs.append(pack)
        a, b = packs
        def path(pack, suffix):
            return f"/packs/{pack['id']}/{suffix}"
        users = []
        for pack, name in [(a, "Alice"), (b, "Bob"), (a, "Forwarded invite")]:
            code, user = call("POST", path(pack, "join"), payload=dict(inviteCode=pack["inviteCode"], displayName=name))
            expect(code == 200 and user["status"] == "pending", name + " starts pending")
            users.append(user)
        alice, bob, forwarded = users
        expect(call("GET", path(a, "manifest"), forwarded["token"])[0] == 403, "Forwarded invite does not authorize metadata")
        for pack, user in [(a, alice), (b, bob)]:
            expect(call("PATCH", path(pack, "members/" + user["memberId"]), token, dict(status="approved", role="player"))[0] == 200, user["name"] + " approved")
        content = jar("custom build 1")
        digest = hashlib.sha256(content).hexdigest()
        file = dict(path="mods/custom.jar", sha256=digest, size=len(content), group="base")
        release = dict(name=a["name"], minecraftVersion="1.21.1", loader="fabric", loaderVersion="0.16.10", files=[file], notes="First custom build", expectedReleaseId=None)
        expect(call("PUT", path(a, "blobs/" + digest), alice["token"], content)[0] == 403, "Player cannot upload")
        expect(call("PUT", path(a, "blobs/" + digest), token, content)[0] == 200, "Owner uploads verified custom JAR")
        expect(call("GET", path(a, "blobs/" + digest), alice["token"])[0] == 404, "Unpublished staged blob is inaccessible")
        code, published = call("POST", path(a, "releases"), token, release)
        expect(code == 200, "Owner atomically publishes complete release")
        expect(call("GET", path(a, "manifest"), alice["token"])[1]["releaseId"] == published["releaseId"], "Approved player reads manifest")
        expect(call("GET", path(a, "blobs/" + digest), alice["token"], raw=True)[1] == content, "Approved player downloads exact verified JAR")
        expect(call("GET", path(a, "manifest"), bob["token"])[0] == 403, "Other pack member denied metadata")
        expect(call("GET", path(a, "blobs/" + digest), bob["token"])[0] == 403, "Other pack member denied blob")
        expect(call("GET", path(b, "blobs/" + digest), bob["token"])[0] == 404, "Blob hash cannot bypass pack scoping")
        expect(call("GET", path(a, "blobs/" + digest), forwarded["token"])[0] == 403, "Forwarded invite denied downloads")
        expect(call("POST", path(a, "releases"), alice["token"], release)[0] == 403, "Player cannot publish")
        expect(call("GET", path(a, "members"), alice["token"])[0] == 403, "Player cannot read member list")
        expect(call("POST", path(a, "releases"), token, release)[0] == 409, "Stale publish rejected")
        release["expectedReleaseId"] = published["releaseId"]
        release["files"] = [{**file, "path": "mods/../options.txt.jar"}]
        expect(call("POST", path(a, "releases"), token, release)[0] == 400, "Path traversal rejected")
        release["files"] = [{**file, "path": "mods/CON.jar"}]
        expect(call("POST", path(a, "releases"), token, release)[0] == 400, "Windows reserved filename rejected")
        release["files"] = [{**file, "sha256": "a" * 64}]
        expect(call("POST", path(a, "releases"), token, release)[0] == 409, "Missing blob cannot be published")
        expect(call("GET", path(a, "manifest"), alice["token"])[1]["releaseId"] == published["releaseId"], "Failed publish preserves previous release")
        corrupt = b"not a ZIP archive"
        expect(call("PUT", path(a, "blobs/" + hashlib.sha256(corrupt).hexdigest()), token, corrupt)[0] == 400, "Non-JAR upload rejected")
        expect(call("PUT", path(a, "blobs/" + "b" * 64), token, content)[0] == 400, "Incorrect upload hash rejected")
        identity_files = []
        for index in [1, 2]:
            identity_content = jar("identity build " + str(index), "same_mod_id")
            identity_hash = hashlib.sha256(identity_content).hexdigest()
            call("PUT", path(a, "blobs/" + identity_hash), token, identity_content)
            call("PUT", path(b, "blobs/" + identity_hash), token, identity_content)
            identity_files.append(dict(path=f"mods/identity-{index}.jar", sha256=identity_hash, size=len(identity_content), group="base"))
        release["files"] = identity_files
        expect(call("POST", path(a, "releases"), token, release)[0] == 400, "Duplicate primary mod IDs rejected across filenames")
        release["files"] = [identity_files[0], {**identity_files[1], "group": "voxy"}]
        expect(call("POST", path(a, "releases"), token, release)[0] == 400, "Optional mod cannot duplicate a base mod ID")
        exclusive = {**release, "name": b["name"], "expectedReleaseId": None, "files": [{**identity_files[0], "group": "voxy"}, {**identity_files[1], "group": "distanthorizons"}]}
        expect(call("POST", path(b, "releases"), token, exclusive)[0] == 200, "Exclusive terrain groups may use different builds of the same mod")
        status = call("GET", "/owner/status", token)[1]
        expect(status["lastPlayerName"] == "Alice" and status["successfulPlayerRequests"] >= 2, "Host records approved player reachability evidence")
        expect(call("GET", "/owner/status", alice["token"])[0] == 403, "Host telemetry restricted to administrator")
        expect(call("PATCH", path(a, "members/" + alice["memberId"]), token, dict(status="approved", role="owner"))[0] == 200, "Administrator delegates one pack owner")
        expect([pack["id"] for pack in call("GET", "/owner/packs", alice["token"])[1]] == [a["id"]], "Delegated owner listing is pack scoped")
        expect(call("GET", path(a, "members"), alice["token"])[0] == 200, "Delegated owner can manage their pack")
        expect(call("GET", path(b, "members"), alice["token"])[0] == 403, "Delegated owner cannot manage another pack")
        expect(call("POST", "/owner/packs", alice["token"], dict(name="Forbidden", minecraftVersion="1.21.1", loader="fabric", loaderVersion="0.16.10"))[0] == 403, "Delegated owner cannot create host packs")
        expect(call("PATCH", path(a, "members/" + forwarded["memberId"]), alice["token"], dict(status="approved", role="owner"))[0] == 403, "Delegated owner cannot grant owner privilege")
        expect(call("PATCH", path(a, "members/" + alice["memberId"]), token, dict(status="removed", role="player"))[0] == 200, "Owner removes member")
        expect(call("GET", path(a, "manifest"), alice["token"])[0] == 403, "Removal immediately revokes metadata")
        expect(call("GET", path(a, "blobs/" + digest), alice["token"])[0] == 403, "Removal immediately revokes files")
        call("DELETE", path(a, "invite"), token)
        expect(call("POST", path(a, "join"), payload=dict(inviteCode=a["inviteCode"], displayName="Old invite"))[0] == 404, "Invite rotation invalidates old links")
        process.terminate()
        process.wait(timeout=10)
        start()
        expect(call("GET", path(a, "manifest"), token)[1]["releaseId"] == published["releaseId"], "Complete release survives host restart")
        expect(call("GET", path(a, "manifest"), alice["token"])[0] == 403, "Member revocation survives host restart")
        print(f"\n{len(checks)} host security checks passed.")
    finally:
        if process and process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
        log.close()
