"""
Sign the download-stats workflow in to the Microsoft Store, once, from your own PC.

Run it on your PC:  python .github/store_sign_in.py
Your browser opens; sign in as stats@frishberg.onmicrosoft.com and approve in Authenticator. The script then
copies a refresh token to your clipboard. Paste it into the repo's STORE_REFRESH_TOKEN secret
(GitHub → Watts-Left → Settings → Secrets and variables → Actions). The daily run renews it from then on.

Why not the device code the workflow used to show: the tenant's security defaults now block device code
sign-in (AADSTS530035), and a refresh token that came from one is refused too. This is an ordinary browser
sign-in (authorization code with PKCE, to http://localhost), which security defaults allow.
"""
import base64
import hashlib
import http.server
import json
import os
import secrets
import subprocess
import sys
import threading
import urllib.error
import urllib.parse
import urllib.request
import webbrowser

TENANT = "cf4276a7-667e-4efe-908e-0ea326086144"  # frishberg.onmicrosoft.com
CLIENT = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"  # Microsoft's public Azure CLI client, as in download_stats.py
RESOURCE = "https://manage.devcenter.microsoft.com"
LOGIN = f"https://login.microsoftonline.com/{TENANT}/oauth2"
USER = "stats@frishberg.onmicrosoft.com"


def main() -> int:
    verifier = secrets.token_urlsafe(64)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    state = secrets.token_urlsafe(16)
    result = {}

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            if "code" in query or "error" in query:
                result.update({k: v[0] for k, v in query.items()})
                ok = "code" in query and query.get("state", [""])[0] == state
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.end_headers()
                self.wfile.write(("<p>Signed in. You can close this tab.</p>" if ok else
                                  "<p>Sign-in didn't finish. Check the terminal.</p>").encode())
                threading.Thread(target=server.shutdown, daemon=True).start()
            else:
                self.send_response(204)
                self.end_headers()

        def log_message(self, *args):
            pass

    server = http.server.HTTPServer(("localhost", 0), Handler)
    redirect = f"http://localhost:{server.server_port}"
    url = LOGIN + "/authorize?" + urllib.parse.urlencode({
        "client_id": CLIENT, "response_type": "code", "redirect_uri": redirect, "resource": RESOURCE,
        "state": state, "code_challenge": challenge, "code_challenge_method": "S256",
        "login_hint": USER, "prompt": "login",
    })
    print(f"Opening your browser. Sign in as {USER}.\nIf it doesn't open, paste this into a browser:\n{url}\n")
    webbrowser.open(url)
    server.serve_forever()

    if result.get("state") != state or "code" not in result:
        print("Sign-in failed:", result.get("error_description") or result.get("error") or "no code came back")
        return 1
    form = urllib.parse.urlencode({
        "grant_type": "authorization_code", "client_id": CLIENT, "code": result["code"],
        "redirect_uri": redirect, "resource": RESOURCE, "code_verifier": verifier,
    }).encode()
    try:
        with urllib.request.urlopen(urllib.request.Request(LOGIN + "/token", data=form), timeout=60) as r:
            token = json.load(r)
    except urllib.error.HTTPError as e:
        token = json.loads(e.read() or b"{}")
    if "refresh_token" not in token:
        print("Sign-in failed:", token.get("error_description", token))
        return 1

    # Prove it works before handing it over: the same Store analytics call the workflow makes.
    probe = urllib.request.Request(
        "https://manage.devcenter.microsoft.com/v1.0/my/analytics/installs?applicationId=9N39TLT3SXV6"
        "&startDate=09/01/2026&endDate=09/30/2026&aggregationLevel=month",
        headers={"Authorization": "Bearer " + token["access_token"]})
    try:
        with urllib.request.urlopen(probe, timeout=60) as r:
            rows = json.load(r).get("Value", [])
        print(f"Store analytics answered ({sum(x.get('successfulInstallCount', 0) for x in rows)} installs in September).")
    except urllib.error.HTTPError as e:
        print(f"Signed in, but the Store analytics call failed ({e.code}). Check that {USER} has analytics access.")
        return 1

    if sys.platform == "win32":
        subprocess.run("clip", input=token["refresh_token"].encode("ascii"), check=True)
        print("\nThe refresh token is on your clipboard. Paste it into the STORE_REFRESH_TOKEN secret.")
    else:
        path = os.path.abspath("store_refresh_token.txt")
        with open(path, "w") as f:
            f.write(token["refresh_token"])
        print(f"\nThe refresh token is in {path}. Paste it into the STORE_REFRESH_TOKEN secret, then delete the file.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
