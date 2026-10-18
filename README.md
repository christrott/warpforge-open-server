# WarpforgeOpenServer

A local stand-in for Warpforge's deleted PlayFab title (`A3CCB`). The BepInEx plugin in `../NetLogger` redirects every `*.playfabapi.com` request here.

## First time only: trust the HTTPS certificate

The game connects over HTTPS, so Windows has to trust the local development certificate. Run this once and click **Yes** on the Windows security prompt:

```bash
dotnet dev-certs https --trust
```

## Run

```bash
dotnet run
```

Run it from this folder, and leave it running while you play. It listens on `https://localhost:5443` (used by default) and `http://localhost:5080`.

Each request is printed to the console and saved to `logs/requests.log`:

- **green:** handled
- **yellow `UNHANDLED`:** the game called something that has no real implementation yet. It got an empty success response. Implement these next.
- **red `???`:** a request that didn't match `POST /<Api>/<Method>`

## Data you can edit (restart the server after editing)

| File | What it is |
|---|---|
| `data/player.json` | Your account: display name, user data, read-only data, inventory, currencies, statistics |
| `data/titledata.json` | PlayFab title data as `"Key": "string value"`. The game probably keeps a lot of config here. |
| `data/responses/<Method>.json` | Replaces the `data` part of the response for any PlayFab method, e.g. `GetCatalogItems.json` |
| `data/cloudscript/<FunctionName>.json` | Becomes the `FunctionResult` of an `ExecuteCloudScript` / `ExecuteFunction` call with that name |

The canned-file overrides let you try a response without changing any code.

## Pointing the game somewhere else

The redirect target is in `<game>\BepInEx\config\local.warpforge.netlogger.cfg` under `[Redirect] PlayFabUrl`:

- Set it to `http://localhost:5080` if HTTPS fails. Unity may refuse plain HTTP, though.
- Leave it empty to turn the redirect off.
