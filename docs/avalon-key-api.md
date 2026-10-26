# Avalon plugin key API

What the Avalon website needs so that only the team's crafters can use the plugin.

## Website pages

A signed-in crafter can:

1. **Generate a key.** Show it once, with a copy button. It is pasted into the plugin under `/avalon config` → *Avalon key*.
2. **See their keys** (created date, last used) and **revoke** any of them.

Only accounts that are team crafters may generate keys. Removing someone from the crafters should make all their keys stop working.

## Storing keys

- Generate at least 32 random bytes from a secure random source and encode them (base64url or hex), e.g. `avk_` + 43 characters.
- Store only a **hash** (SHA-256 is fine for random keys), never the key itself, plus: account id, created at, last used at, revoked at, optional expiry.
- Look a key up by its hash.

## Endpoint

```
GET /api/plugin/key/verify
Authorization: Bearer <key>
```

HTTPS only. The plugin calls this when it starts, when a key is saved, and every hour.

### Key accepted: `200`

```json
{ "valid": true, "crafterName": "Firstname Lastname", "expiresAt": null }
```

`crafterName` is shown in the plugin. `expiresAt` is an ISO 8601 date or `null`. Update the key's *last used* time.

### Key refused: `401`

```json
{ "valid": false, "reason": "revoked" }
```

`reason` is one of:

| reason        | when                                         |
|---------------|----------------------------------------------|
| `unknown`     | no key with that hash, or no header          |
| `revoked`     | the crafter (or an admin) revoked it         |
| `expired`     | past its expiry date                         |
| `not_in_team` | the account is no longer a team crafter      |

Any other status (5xx, etc.) makes the plugin show "can't reach the Avalon website" and stay locked.

### Rate limiting

Limit failed attempts per IP (e.g. 20 per minute) so keys can't be guessed.

## Quotes from the plugin

The verify response also has `"canQuote": true|false` — true for admins, managers and members with the
Discord Staff role (the people who can press **Send quote** in a ticket). Both endpoints below need such a key
(`401` for a bad key, `403` when the owner can't quote). Failed keys count toward the same rate limit.

### `GET /api/plugin/requests`

Requests a quote can be sent for (accepted or in progress, ticket not closed), newest first:

```json
[{ "id": "req-…", "number": 12, "client": "Name", "service": "Crafting", "artist": "Ayame", "status": "accepted",
   "quote": { "status": "pending", "price": "84,250 gil" } | null,
   "teamcraft": { "listId": "…", "listName": "…" } | null, "hasTicket": true }]
```

### `POST /api/plugin/requests/:id/quote`

```json
{ "service": "…", "price": "…", "details": "…", "dueDate": "…", "teamcraftListId": "…", "teamcraftListName": "…" }
```

Same limits as the Discord quote form (120 / 60 / 1000 / 60 characters). Sends or replaces the quote exactly like
**Send quote** (the client must accept it), remembers the Teamcraft list on the request, and posts the quote in the
Discord ticket. Returns `{ "ok": true, "number": 12, "postedToTicket": true }`; `postedToTicket` is false when the
request has no ticket or the bot is offline (the client can still answer under **My requests**).
