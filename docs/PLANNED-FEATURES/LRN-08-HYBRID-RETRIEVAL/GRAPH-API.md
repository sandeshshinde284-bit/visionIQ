# LRN-07 graph query API (for the Impact Agent backend)

`GET /api/v1/graph/work-items/{project}/{workItemId}?hops=1..3` (default 2) returns the authorized neighbourhood
of an Azure DevOps work item: nodes (kind, native ID, source URL, safe metadata) and edges (relation,
classification). It is exposed **only** when all `VISIONIQ_AUTH_*` settings and ADO access are configured.

## Security model

| Check | Rule |
|---|---|
| Token | Microsoft Entra ID access token validated by Microsoft's JwtBearer library: signature (tenant signing keys), tenant-specific issuer (v1 or v2), audience = VisionIQ, lifetime (2 min skew) |
| Caller | `azp` (v2) / `appid` (v1) must be in `VISIONIQ_AUTH_ALLOWED_CALLERS` |
| User | Delegated token only (`scp` must contain the required scope; app-only tokens are rejected); `oid` and an e-mail style `preferred_username`/`upn` required |
| ADO identity | The user's sign-in name is used as their ADO identity; live ADO project-team membership is checked for every returned item |
| Code evidence | Files, symbols and tests are readable by live members of the ADO project the repository was scanned for (scanner run with `--ado-source-instance`; otherwise hidden) |
| Confluence pages | Readable only when Confluence Cloud's live permission check (`/rest/api/content/{id}/permission/check`) says this user can read the page right now: space, page and inherited ancestor restrictions are evaluated by Confluence. The user's Atlassian account ID comes from `CONFLUENCE_IDENTITY_MAP_FILE` (JSON: e-mail to account ID); unmapped users, Data Center pages and ACL snapshots stay hidden |
| Results | Only authorized nodes and edges; "not found" and "not allowed" both return 404; no counts of hidden items; `Cache-Control: no-store` |

Nothing is ever taken from request bodies or custom headers. Any missing or unexpected value denies.

## Configuration

| Setting | Meaning |
|---|---|
| `VISIONIQ_AUTH_TENANT_ID` | Your Entra tenant **GUID** (`common`/`organizations` are rejected) |
| `VISIONIQ_AUTH_AUDIENCES` | VisionIQ API's Application ID URI and/or client ID, comma-separated |
| `VISIONIQ_AUTH_SCOPE` | Required delegated scope (default `Graph.Read`) |
| `VISIONIQ_AUTH_ALLOWED_CALLERS` | Impact Agent backend's application (client) ID(s), comma-separated |
| `ADO_ORG_URL`, `ADO_PROJECTS`, `ADO_READ_PAT` | ADO access used to check project membership |

A partial configuration stops the API at startup with a clear error.

## Confluence identity (decision for your Atlassian administrators)

Atlassian does not reliably expose e-mail to account ID lookups, so VisionIQ never guesses. Provide a JSON
map `{"ana@contoso.com": "<atlassian account id>", ...}` in `CONFLUENCE_IDENTITY_MAP_FILE` (for example
exported from Atlassian Guard / your SSO directory). Without it, Confluence evidence stays hidden.

## Future Entra setup (not done yet; needs your Entra administrators)

1. Register **VisionIQ API** as an Entra application; expose an API with an Application ID URI and a delegated
   scope `Graph.Read`; set `accessTokenAcceptedVersion` to 2.
2. Grant the **Impact Agent backend** application permission to `Graph.Read` (admin consent).
3. Impact Agent uses the **On-Behalf-Of** flow: it exchanges the signed-in engineer's token for a VisionIQ
   token and calls this API with it.
4. Confirm that engineers' Entra sign-in names match their Azure DevOps identities (otherwise access is denied).

## Testing

Authorization rules and query behaviour are unit tested. Token handling (valid, forged, expired, wrong
audience, wrong tenant, unapproved caller, app-only, missing scope, no token, endpoint absent when not
configured) runs in CI with Microsoft's real libraries and locally signed test tokens; no production Entra
registration is needed.
