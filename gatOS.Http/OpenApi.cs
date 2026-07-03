namespace gatOS.Http;

/// <summary>
///     A small hand-written OpenAPI 3.1 document for the <c>/v1</c> surface
///     (KSA_GAME_INTEGRATION_PLAN Part 6 T2): players point a codegen at <c>GET /v1/openapi.json</c>
///     to build a typed client in any language. It documents the read projections, the SSE event
///     stream, the sim-time long-poll, and the single generic command endpoint.
/// </summary>
internal static class OpenApi
{
    internal const string Document =
        """
        {
          "openapi": "3.1.0",
          "info": {
            "title": "gatOS /sim HTTP API",
            "version": "1",
            "description": "The magic HTTP transport mirroring the /sim file tree. Reads are a JSON projection of the latest published telemetry snapshot (atomic); writes go through one generic command endpoint carrying the transport-agnostic SimCommand shape."
          },
          "servers": [{ "url": "http://sim:4242/v1", "description": "guest-side (slirp 10.0.2.2); $GATOS_HTTP is the always-correct base" }],
          "paths": {
            "/snapshot": { "get": { "summary": "The whole telemetry snapshot (atomic)", "responses": { "200": { "description": "SimSnapshot" } } } },
            "/time": { "get": { "summary": "ut, warp, sim_dt, warp_speeds, auto-warp", "responses": { "200": { "description": "time" } } } },
            "/time/wait": { "get": { "summary": "Long-poll until sim time reaches 'until'", "parameters": [{ "name": "until", "in": "query", "required": true, "schema": { "type": "number" } }], "responses": { "200": { "description": "{reached_ut}" } } } },
            "/status": { "get": { "summary": "Integration health + bound transports", "responses": { "200": { "description": "status" } } } },
            "/system": { "get": { "summary": "Current star-system summary", "responses": { "200": { "description": "SystemSnapshot" } } } },
            "/bodies": { "get": { "summary": "Celestial bodies", "responses": { "200": { "description": "BodySnapshot[]" } } } },
            "/bodies/{id}": { "get": { "summary": "One body", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "BodySnapshot" }, "404": { "description": "gone" } } } },
            "/vessels": { "get": { "summary": "Vessel ids", "responses": { "200": { "description": "string[]" } } } },
            "/vessels/{id}": { "get": { "summary": "One vessel", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "VesselSnapshot" }, "404": { "description": "gone" } } } },
            "/vessels/{id}/telemetry": { "get": { "summary": "Compact per-vessel telemetry document", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "telemetry" } } } },
            "/vessels/{id}/stream": { "get": { "summary": "Server-Sent Events of the per-vessel telemetry stream line (the HTTP twin of /sim/.../stream)", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "text/event-stream" } } } },
            "/fs/{path}": {
              "get": { "summary": "Read one /sim field by path as text/plain (the field-level filesystem mirror); add ?stream=1 for an SSE feed of the value on change", "parameters": [{ "name": "path", "in": "path", "required": true, "schema": { "type": "string" }, "description": "the /sim-relative path, e.g. vessels/by-id/<id>/altitude/radar or time/ut" }], "responses": { "200": { "description": "the raw value (text/plain), or text/event-stream when ?stream=1" }, "404": { "description": "no such field" } } },
              "post": { "summary": "Write one /sim field (the `echo value > file` shape; control/debug points actuate, errno on failure)", "parameters": [{ "name": "path", "in": "path", "required": true, "schema": { "type": "string" } }], "requestBody": { "required": true, "content": { "text/plain": { "schema": { "type": "string" } } } }, "responses": { "200": { "description": "{outcome:ok}" }, "400": { "description": "EINVAL" }, "403": { "description": "EACCES" }, "404": { "description": "ENOENT" }, "409": { "description": "EBUSY" }, "501": { "description": "EOPNOTSUPP" }, "504": { "description": "ETIMEDOUT" } } }
            },
            "/events": { "get": { "summary": "Server-Sent Events stream of sim events", "responses": { "200": { "description": "text/event-stream" } } } },
            "/audio/files": { "get": { "summary": "Uploaded audio clips (name, bytes, version, ready)", "responses": { "200": { "description": "clip list" }, "404": { "description": "audio disabled" } } } },
            "/audio/file/{name}": {
              "put": { "summary": "Binary clip upload (raw body; ?offset=N appends by position for chunking, ?complete=0 keeps the upload open — default commits). POST is an alias.", "parameters": [{ "name": "name", "in": "path", "required": true, "schema": { "type": "string" } }, { "name": "offset", "in": "query", "schema": { "type": "integer", "default": 0 } }, { "name": "complete", "in": "query", "schema": { "type": "integer", "default": 1 } }], "requestBody": { "required": true, "content": { "application/octet-stream": { "schema": { "type": "string", "format": "binary" } } } }, "responses": { "200": { "description": "{outcome:ok,name,bytes,ready}" }, "400": { "description": "EINVAL (bad name / non-sequential offset)" }, "413": { "description": "EFBIG (per-clip cap)" }, "507": { "description": "ENOSPC (store/count cap)" } } },
              "delete": { "summary": "Evict a clip (playing channels finish naturally)", "parameters": [{ "name": "name", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "{outcome:ok}" }, "404": { "description": "ENOENT" } } }
            },
            "/command": {
              "post": {
                "summary": "Submit one control command (synchronous; errno on failure)",
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object",
                  "required": ["vessel_id", "action"],
                  "properties": {
                    "vessel_id": { "type": "string" },
                    "action": { "type": "string", "description": "e.g. engine.active, vessel.ignite, vessel.attitude_mode, debug.refill_battery" },
                    "ordinal": { "type": "integer", "default": -1 },
                    "value": { "type": "number" },
                    "values": { "type": "array", "items": { "type": "number" } },
                    "token": { "type": "string" },
                    "aux": { "type": "string", "description": "secondary symbolic arg (audio.play channel id)" }
                  }
                } } } },
                "responses": {
                  "200": { "description": "{outcome:ok}" },
                  "400": { "description": "EINVAL" }, "403": { "description": "EACCES" },
                  "404": { "description": "ENOENT" }, "409": { "description": "EBUSY" },
                  "501": { "description": "EOPNOTSUPP" }, "504": { "description": "ETIMEDOUT" }
                }
              }
            }
          }
        }
        """;
}
