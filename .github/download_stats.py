"""
Count Watt's Left downloads and print them as JSON. Run daily by
.github/workflows/download-stats.yml, which publishes the output as
downloads.json on the `stats` branch:

  https://raw.githubusercontent.com/frishberg/Watts-Left/stats/downloads.json

  github  .exe downloads across every GitHub release
  store   successful Microsoft Store installs since launch (null without a Store sign-in)
  total   github + store

Store auth: individual developer accounts can't create the Entra application the
Store analytics docs describe, so this signs in as a Partner Center user through
Microsoft's public Azure CLI client and keeps that sign-in alive with its refresh
token. Each run gets a new refresh token, which the workflow saves back into the
repo secret. Use a user with read-only analytics access and no admin roles
(stats@frishberg.onmicrosoft.com), so a leaked token can only read analytics.

Env vars:
  GITHUB_TOKEN          optional, lifts the GitHub API rate limit
  STORE_REFRESH_TOKEN   refresh token from the last run; without it store is null
  STORE_SIGN_IN         "true" to sign in from scratch with a device code (shown in the log). Blocked since
                        October 2026 by the tenant's security defaults; sign in with .github/store_sign_in.py instead
  NEW_REFRESH_TOKEN     file to write the new refresh token to
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

REPO = "frishberg/Watts-Left"
STORE_ID = "9N39TLT3SXV6"
SINCE = "09/01/2026"  # before the first Store release; the Store API takes M/D/YYYY
TENANT = "cf4276a7-667e-4efe-908e-0ea326086144"  # frishberg.onmicrosoft.com
CLIENT = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"  # Microsoft's public Azure CLI client
RESOURCE = "https://manage.devcenter.microsoft.com"
LOGIN = f"https://login.microsoftonline.com/{TENANT}/oauth2"


def get_json(url, headers=None, data=None):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def post_form(url, form):
    """POST a form to the sign-in endpoint; error replies come back as JSON too."""
    try:
        return get_json(url, data=urllib.parse.urlencode(form).encode())
    except urllib.error.HTTPError as e:
        return json.loads(e.read() or b"{}")


def github_downloads():
    headers = {"Accept": "application/vnd.github+json"}
    if os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    total, page = 0, 1
    while True:
        releases = get_json(f"https://api.github.com/repos/{REPO}/releases?per_page=100&page={page}", headers)
        total += sum(a["download_count"] for r in releases for a in r["assets"] if a["name"].endswith(".exe"))
        if len(releases) < 100:
            return total
        page += 1


def device_code_sign_in():
    code = post_form(LOGIN + "/devicecode", {"client_id": CLIENT, "resource": RESOURCE})
    if "device_code" not in code:
        sys.exit(f"Sign-in failed: {code.get('error_description', code)}")
    print(code["message"] + " Sign in as stats@frishberg.onmicrosoft.com.", file=sys.stderr, flush=True)
    deadline = time.time() + int(code["expires_in"])
    token = {}
    while time.time() < deadline:
        time.sleep(int(code.get("interval", 5)))
        token = post_form(LOGIN + "/token", {
            "grant_type": "device_code", "client_id": CLIENT, "resource": RESOURCE, "code": code["device_code"]})
        if "access_token" in token:
            return token
        if token.get("error") not in ("authorization_pending", "slow_down"):
            break
    sys.exit(f"Sign-in failed: {token.get('error_description', 'timed out')}")


def store_token():
    if os.environ.get("STORE_SIGN_IN") == "true":
        token = device_code_sign_in()
    elif os.environ.get("STORE_REFRESH_TOKEN"):
        token = post_form(LOGIN + "/token", {
            "grant_type": "refresh_token", "client_id": CLIENT, "resource": RESOURCE,
            "refresh_token": os.environ["STORE_REFRESH_TOKEN"].strip()})
        if "access_token" not in token:
            sys.exit("Store sign-in was rejected (expired, revoked, or STORE_REFRESH_TOKEN was pasted incompletely); "
                     "on your PC run 'python .github/store_sign_in.py' and paste the new token into STORE_REFRESH_TOKEN.\n"
                     + token.get("error_description", ""))
    else:
        return None
    if os.environ.get("NEW_REFRESH_TOKEN") and token.get("refresh_token"):
        with open(os.environ["NEW_REFRESH_TOKEN"], "w") as f:
            f.write(token["refresh_token"])
    return token["access_token"]


def store_installs():
    """Store installs since launch, total and by market (ISO 3166 country code), or (None, None)."""
    token = store_token()
    if not token:
        return None, None
    url = "https://manage.devcenter.microsoft.com/v1.0/my/analytics/installs?" + urllib.parse.urlencode({
        "applicationId": STORE_ID,
        "startDate": SINCE,
        "endDate": datetime.now(timezone.utc).strftime("%m/%d/%Y"),
        "aggregationLevel": "month",
        "groupby": "market",
    })
    markets = {}
    while url:
        page = get_json(url, {"Authorization": "Bearer " + token})
        for row in page.get("Value", []):
            market = row.get("market") or "Unknown"
            markets[market] = markets.get(market, 0) + row.get("successfulInstallCount", 0)
        url = page.get("@nextLink") and urllib.parse.urljoin(url, page["@nextLink"])
    total = sum(markets.values())
    return total, dict(sorted(markets.items(), key=lambda kv: (-kv[1], kv[0])))


def rounded(n):
    """For the badge: rounded down, so it never claims more than was counted. 577 -> "500+", 1,140 -> "1.1k+",
    23,900 -> "23k+". Under 100 it's the exact number."""
    if n < 100:
        return str(n)
    if n < 1000:
        return f"{n // 100 * 100}+"
    if n < 10_000:
        k = n // 100 / 10
        return f"{k:g}k+"
    return f"{n // 1000}k+"


def main() -> int:
    github = github_downloads()
    store, markets = store_installs()
    total = github + (store or 0)
    json.dump({
        "total": total,
        "total_label": rounded(total),
        "github": github,
        "store": store,
        # Store installs only; GitHub doesn't record where .exe downloads come from.
        "countries": None if markets is None else sum(1 for m in markets if m != "Unknown"),
        "store_by_country": markets,
        "updated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
