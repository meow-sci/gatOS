export interface McpField {
  name: string;
  type: string;
  required?: boolean;
  default?: string;
  description: string;
}

export interface McpOperation {
  name: string;
  action?: string;
  description: string;
}

export interface McpReferenceEntry {
  name: string;
  kind: "tool" | "resource";
  category: string;
  summary: string;
  useWhen: string;
  gate: string;
  fields?: McpField[];
  operations?: McpOperation[];
  returns: string;
  example: string;
  errors?: string[];
  notes?: string[];
  equivalent?: string;
}

const listFields: McpField[] = [
  {
    name: "limit",
    type: "integer",
    default: "50",
    description: "Entities per page. Valid range: 1–1000.",
  },
  {
    name: "cursor",
    type: "string | null",
    default: "null",
    description: "Opaque continuation cursor returned by the previous page.",
  },
];

const includeField: McpField = {
  name: "include",
  type: "string[] | null",
  default: "null",
  description:
    "Sections to return: flight, orbit, environment, propulsion, resources, power, control, modules, encounters, parts, paint, or all.",
};

const commandFields: McpField[] = [
  {
    name: "action",
    type: "string",
    required: true,
    description: "Canonical action key from gatos.get_capabilities.",
  },
  {
    name: "vessel_id",
    type: "string",
    default: '""',
    description: "Raw vessel id. Global actions use an empty string.",
  },
  {
    name: "ordinal",
    type: "integer",
    default: "-1",
    description: "Module or entity ordinal where the action addresses one item.",
  },
  { name: "value", type: "number", default: "0", description: "Primary scalar value." },
  {
    name: "values",
    type: "number[] | null",
    default: "null",
    description: "Vector, quaternion, color, or other ordered numeric payload.",
  },
  {
    name: "token",
    type: "string | null",
    default: "null",
    description: "Named mode, id, field, or other action-specific token.",
  },
  {
    name: "aux",
    type: "string | null",
    default: "null",
    description: "Secondary action-specific token.",
  },
];

const logicalFields: McpField[] = [
  {
    name: "operation",
    type: "string",
    required: true,
    description: "Logical operation listed below. A dotted canonical action is also accepted.",
  },
  { name: "value", type: "number", default: "0", description: "Operation-specific scalar." },
  {
    name: "values",
    type: "number[] | null",
    default: "null",
    description: "Operation-specific numeric vector.",
  },
  {
    name: "token",
    type: "string | null",
    default: "null",
    description: "Operation-specific named value.",
  },
  {
    name: "aux",
    type: "string | null",
    default: "null",
    description: "Operation-specific secondary value.",
  },
];

export const mcpReference: Record<string, McpReferenceEntry> = {
  "gatos.get_world": {
    name: "gatos.get_world",
    kind: "tool",
    category: "World and discovery",
    summary: "Read a concise simulation overview or the complete current world snapshot.",
    useWhen:
      "Starting a planning turn, checking simulation freshness, or obtaining the entire immutable snapshot in one call.",
    gate: "Telemetry master; disabled detail streams remain absent from the snapshot.",
    fields: [
      {
        name: "detail",
        type: '"summary" | "full"',
        default: '"summary"',
        description:
          "Summary returns time, warp, status, gates, and entity indexes; full returns the complete SimSnapshot projection.",
      },
    ],
    returns:
      "Summary world metadata and indexes, or the complete current world document. Both include snapshot_sequence and ut.",
    example: '{"detail":"summary"}',
    equivalent: "gatos://world",
    notes: [
      "Use full deliberately: it is complete and is never output-size-truncated.",
      "The TTY-only /sim/display surface is not present.",
    ],
  },
  "gatos.list_celestials": {
    name: "gatos.list_celestials",
    kind: "tool",
    category: "Celestials",
    summary: "List celestial summaries in deterministic body order.",
    useWhen: "Discovering raw body ids before orbit, navigation, or geodetic operations.",
    gate: "Body telemetry must be enabled.",
    fields: listFields,
    returns: "A page of celestial summary objects plus next_cursor, snapshot_sequence, and ut.",
    example: '{"limit":50}',
    equivalent: "gatos://celestials",
    errors: ["EINVAL for a limit outside 1–1000 or a cursor for another collection."],
  },
  "gatos.get_celestial": {
    name: "gatos.get_celestial",
    kind: "tool",
    category: "Celestials",
    summary: "Read one complete celestial body snapshot by raw KSA id.",
    useWhen: "You need physical, orbital, atmosphere, rotation, or hierarchy data for one body.",
    gate: "Body telemetry must be enabled.",
    fields: [
      {
        name: "id",
        type: "string",
        required: true,
        description: "Raw celestial id returned by list_celestials.",
      },
    ],
    returns: "The complete BodySnapshot plus snapshot_sequence and ut.",
    example: '{"id":"Kerbin"}',
    equivalent: "gatos://celestials/{id}",
    errors: ["ENOENT when the id is not present in the current snapshot."],
  },
  "gatos.list_vessels": {
    name: "gatos.list_vessels",
    kind: "tool",
    category: "Vessels and kittens",
    summary: "List and filter vessel summaries.",
    useWhen:
      "Discovering raw vessel ids or narrowing the live set before requesting a full vessel.",
    gate: "Vessel telemetry must be enabled.",
    fields: [
      ...listFields,
      {
        name: "controlled",
        type: "boolean | null",
        default: "null",
        description: "Filter by current player-controlled state.",
      },
      {
        name: "controllable",
        type: "boolean | null",
        default: "null",
        description: "Filter by whether KSA accepts ordinary flight control.",
      },
      {
        name: "is_kitten",
        type: "boolean | null",
        default: "null",
        description: "Filter EVA kitten vessels.",
      },
      {
        name: "parent_body",
        type: "string | null",
        default: "null",
        description: "Filter by raw parent celestial id.",
      },
      {
        name: "situation",
        type: "string | null",
        default: "null",
        description: "Filter by the published vessel situation token.",
      },
    ],
    returns: "A filtered page of vessel summaries plus next_cursor, snapshot_sequence, and ut.",
    example: '{"controllable":true,"parent_body":"Kerbin","limit":100}',
    equivalent: "gatos://vessels",
  },
  "gatos.get_vessel": {
    name: "gatos.get_vessel",
    kind: "tool",
    category: "Vessels and kittens",
    summary: "Read selected complete logical sections for one vessel.",
    useWhen: "Planning flight or inspecting fitted systems without walking the /sim field tree.",
    gate: "The requested telemetry detail streams must be enabled.",
    fields: [
      {
        name: "id",
        type: "string",
        required: true,
        description: 'Raw vessel id or the alias "active".',
      },
      includeField,
    ],
    returns:
      "Identity and core state plus every requested section, with all matching nested modules or parts; snapshot_sequence and ut are included.",
    example: '{"id":"active","include":["flight","orbit","propulsion","resources"]}',
    equivalent: "gatos://vessels/{id}",
    errors: [
      "ENOENT for a missing id or when active has no current vessel.",
      "EINVAL for an unknown include section.",
    ],
    notes: [
      "There is no result-size truncation. Request parts, paint, or all only when that nested state is actually useful.",
    ],
  },
  "gatos.list_kittens": {
    name: "gatos.list_kittens",
    kind: "tool",
    category: "Vessels and kittens",
    summary: "List EVA kittens as a filtered vessel collection.",
    useWhen: "Finding kittens for EVA navigation, camera, cosmetic, or cabin operations.",
    gate: "Vessel telemetry must be enabled.",
    fields: listFields,
    returns:
      "A page containing only VesselSnapshot entries where is_kitten is true, plus cursor and freshness metadata.",
    example: '{"limit":50}',
    equivalent: "gatos://kittens",
  },
  "gatos.get_kitten": {
    name: "gatos.get_kitten",
    kind: "tool",
    category: "Vessels and kittens",
    summary: "Read a kitten through the same complete vessel telemetry model.",
    useWhen:
      "You have a kitten id and need its flight, environment, parts, or other vessel sections.",
    gate: "Vessel telemetry must be enabled.",
    fields: [
      {
        name: "id",
        type: "string",
        required: true,
        description: "Raw id of a vessel whose is_kitten field is true.",
      },
      includeField,
    ],
    returns: "The selected vessel document after kitten validation, plus snapshot_sequence and ut.",
    example: '{"id":"Valentina","include":["flight","environment","parts"]}',
    equivalent: "gatos://kittens/{id}",
    errors: [
      "ENOENT for a missing id; EINVAL when the vessel is not a kitten or an include is unknown.",
    ],
  },
  "gatos.get_runtime_state": {
    name: "gatos.get_runtime_state",
    kind: "tool",
    category: "World and discovery",
    summary: "Read the complete published state of one gatOS runtime feature.",
    useWhen: "Inspecting feature-owned state that does not naturally belong to a vessel snapshot.",
    gate: "The selected feature may be disabled; capabilities reports availability.",
    fields: [
      {
        name: "feature",
        type: "string",
        required: true,
        description:
          "camera, schedules, audio, paint, welds, thug_life, face_fx, iva, engine_plume, plume_trail, clouds, or terrain.",
      },
    ],
    returns:
      "The selected store/runtime snapshot and the closest available version/status fields, correlated with current snapshot_sequence and ut.",
    example: '{"feature":"paint"}',
    equivalent: "gatos://runtime/{feature}",
    errors: ["EINVAL for an unknown feature; EOPNOTSUPP when its store is disabled."],
  },
  "gatos.get_capabilities": {
    name: "gatos.get_capabilities",
    kind: "tool",
    category: "World and discovery",
    summary: "Discover canonical actions, schemas, phases, gates, limits, and safety metadata.",
    useWhen:
      "At session start, before using optional features, or whenever an operation's units or availability are uncertain.",
    gate: "Always available while MCP is running.",
    fields: [],
    returns:
      "A catalog-generated capability document reflecting current feature gates and transport limits.",
    example: "{}",
    equivalent: "gatos://capabilities",
    notes: [
      "Treat this as the machine-readable authority. It is generated from the same command catalog used for validation.",
    ],
  },
  "gatos.wait": {
    name: "gatos.wait",
    kind: "tool",
    category: "World and discovery",
    summary: "Wait without polling until snapshot, event, or universal-time state changes.",
    useWhen:
      "Coordinating an agent loop with live simulation progress instead of sleeping blindly.",
    gate: "Events require the event telemetry stream; snapshot and UT waits use SnapshotStore.",
    fields: [
      {
        name: "after_sequence",
        type: "integer | null",
        default: "null",
        description: "Return after a snapshot newer than this sequence is published.",
      },
      {
        name: "event_type",
        type: "string | null",
        default: "null",
        description: "Return on the next selected event type.",
      },
      {
        name: "vessel_id",
        type: "string | null",
        default: "null",
        description: "Optional raw vessel filter for event_type.",
      },
      {
        name: "until_ut",
        type: "number | null",
        default: "null",
        description: "Return when simulation UT reaches or exceeds this value in seconds.",
      },
      {
        name: "timeout_ms",
        type: "integer",
        default: "30000",
        description: "Wall-clock timeout from 1 through 120000 ms.",
      },
    ],
    returns: "The first matching condition and current freshness metadata.",
    example:
      '{"after_sequence":812,"event_type":"stage.activated","vessel_id":"Hunter","timeout_ms":30000}',
    errors: [
      "EINVAL when no condition is supplied or timeout is outside bounds.",
      "ETIMEDOUT with retryable=true when no condition matches in time.",
    ],
    notes: [
      "When several conditions are supplied, the first match wins.",
      "Closing the HTTP request cancels the wait.",
    ],
  },
  "gatos.ignite_engines": {
    name: "gatos.ignite_engines",
    kind: "tool",
    category: "Common vessel actions",
    summary: "Ignite all ignitable engines on one vessel.",
    useWhen: "The intent is simply engine ignition and no custom envelope is needed.",
    gate: "control_enabled and vessel authority rules apply.",
    fields: [
      { name: "vessel_id", type: "string", required: true, description: "Raw target vessel id." },
    ],
    returns: "Command result for canonical vessel.ignite.",
    example: '{"vessel_id":"Hunter"}',
    errors: ["ENOENT for a missing vessel; EACCES when control authority is denied."],
    notes: ["Solid rocket ignition is irreversible in normal game physics."],
  },
  "gatos.shutdown_engines": {
    name: "gatos.shutdown_engines",
    kind: "tool",
    category: "Common vessel actions",
    summary: "Shut down all engines on one vessel.",
    useWhen: "The intent is an immediate engine shutdown.",
    gate: "control_enabled and vessel authority rules apply.",
    fields: [
      { name: "vessel_id", type: "string", required: true, description: "Raw target vessel id." },
    ],
    returns: "Command result for canonical vessel.shutdown.",
    example: '{"vessel_id":"Hunter"}',
    errors: ["ENOENT for a missing vessel; EACCES when control authority is denied."],
    notes: ["An ignited SRB cannot be shut down by this action."],
  },
  "gatos.activate_stage": {
    name: "gatos.activate_stage",
    kind: "tool",
    category: "Common vessel actions",
    summary: "Activate the next stage on one vessel.",
    useWhen: "Advancing the vessel staging sequence as a discrete trigger.",
    gate: "control_enabled and vessel authority rules apply.",
    fields: [
      { name: "vessel_id", type: "string", required: true, description: "Raw target vessel id." },
    ],
    returns: "Command result for canonical vessel.stage.",
    example: '{"vessel_id":"Hunter"}',
    errors: ["ENOENT for a missing vessel; EACCES when control authority is denied."],
    notes: ["Staging is a trigger and can decouple or ignite hardware; do not retry blindly."],
  },
  "gatos.vessel_control": {
    name: "gatos.vessel_control",
    kind: "tool",
    category: "Logical controls",
    summary: "Apply one logical vessel-level flight, focus, or rendering operation.",
    useWhen:
      "Changing a whole-vessel setpoint or mode without using a raw canonical action envelope.",
    gate: "Most operations require control_enabled and vessel authority; focus, scale, always_render, and debug take-control have specialized gates.",
    fields: [
      {
        name: "operation",
        type: "string",
        required: true,
        description: "Operation listed below or a dotted canonical action.",
      },
      { name: "vessel_id", type: "string", required: true, description: "Raw target vessel id." },
      {
        name: "ordinal",
        type: "integer",
        default: "-1",
        description: "Normally unused; forwarded for canonical operations.",
      },
      ...logicalFields.slice(1),
    ],
    operations: [
      {
        name: "ignite / shutdown / engine_master / stage",
        action: "vessel.ignite / vessel.shutdown / vessel.engine / vessel.stage",
        description: "Engine and staging triggers or engine master state.",
      },
      {
        name: "throttle / lights / rcs / rcs_mode",
        action: "vessel.*",
        description: "Primary flight-system scalar or enum state.",
      },
      {
        name: "translate / rotate",
        action: "vessel.translate / vessel.rotate",
        description: "Body-axis RCS command vectors.",
      },
      {
        name: "attitude_mode / attitude_frame / attitude_target",
        action: "vessel.attitude_*",
        description: "Flight-computer attitude configuration and target quaternion.",
      },
      {
        name: "burn",
        action: "vessel.burn",
        description: "Burn vector/setpoint using the canonical catalog units.",
      },
      {
        name: "scale / always_render",
        action: "vessel.scale / vessel.always_render",
        description: "Per-vessel visual controls.",
      },
      {
        name: "focus / take_control",
        action: "camera.focus / debug.control_vessel",
        description: "Focus the camera or change the player-controlled vessel.",
      },
    ],
    returns: "The compiled canonical action and command outcome with current freshness metadata.",
    example: '{"operation":"throttle","vessel_id":"Hunter","value":0.72}',
    errors: [
      "EINVAL for an unknown operation or invalid shape/value; ENOENT for a missing vessel; EACCES for gate/authority denial.",
    ],
    notes: [
      "Check controllable before normal flight control; KSA may ignore commands to an uncontrollable vessel.",
    ],
  },
  "gatos.module_control": {
    name: "gatos.module_control",
    kind: "tool",
    category: "Logical controls",
    summary: "Control one indexed engine, RCS unit, light, animation, docking port, or decoupler.",
    useWhen: "A change targets a fitted module rather than the vessel as a whole.",
    gate: "control_enabled; docking pushoff additionally requires debug_namespace.",
    fields: [
      {
        name: "operation",
        type: "string",
        required: true,
        description: "Logical operation listed below or dotted canonical action.",
      },
      { name: "vessel_id", type: "string", required: true, description: "Raw target vessel id." },
      {
        name: "ordinal",
        type: "integer",
        required: true,
        description: "Zero-based module ordinal from the vessel document.",
      },
      ...logicalFields.slice(1),
    ],
    operations: [
      {
        name: "engine_active / engine_minimum_throttle",
        action: "engine.active / engine.min_throttle",
        description: "Engine state and minimum-throttle setting.",
      },
      {
        name: "rcs_active",
        action: "rcs.active",
        description: "Enable or disable one RCS controller.",
      },
      {
        name: "light_on / light_brightness / light_color",
        action: "light.*",
        description: "Light state, intensity, and RGB color.",
      },
      {
        name: "light_outer_angle / light_inner_angle",
        action: "light.*_angle",
        description: "Spotlight cone angles.",
      },
      {
        name: "animation_goal / solar_deployment",
        action: "animation.goal",
        description: "Drive animation or deployment goal.",
      },
      {
        name: "undock / fire_decoupler / pushoff",
        action: "docking.undock / decoupler.fire / debug.docking_pushoff",
        description: "Discrete docking and separation operations.",
      },
    ],
    returns: "Command outcome for the selected module and canonical action.",
    example: '{"operation":"engine_active","vessel_id":"Hunter","ordinal":0,"value":1}',
    errors: [
      "ENOENT for vessel/module lookup; EOPNOTSUPP for a disabled decoupler; EINVAL for invalid values.",
    ],
  },
  "gatos.camera_control": {
    name: "gatos.camera_control",
    kind: "tool",
    category: "Camera and media",
    summary: "Own and direct gatOS's programmable camera.",
    useWhen:
      "Framing a shot, following an entity, changing projection, or controlling camera playback.",
    gate: "camera_enabled; playback also requires schedule_enabled. IVA and Map ownership contexts remain unsupported.",
    fields: logicalFields,
    operations: [
      {
        name: "ownership / take / release",
        action: "camera.enabled / camera.release",
        description: "Acquire or release camera ownership.",
      },
      {
        name: "mode / follow / tidal / map_scope",
        action: "camera.*",
        description: "Select game camera behavior and map scope.",
      },
      {
        name: "position / frame / anchor / geodetic",
        action: "camera.position / frame / anchor / geo",
        description: "Place the camera in CCI, body, anchor, or geodetic terms.",
      },
      {
        name: "orbit_radius / orbit_azimuth / orbit_elevation",
        action: "camera.orbit_*",
        description: "Anchor-relative orbit placement.",
      },
      {
        name: "rotation / aim / aim_target / aim_offset / aim_frame / aim_up / roll",
        action: "camera.*",
        description: "Orient or aim the camera.",
      },
      {
        name: "fov / ortho / ortho_height / smoothing / reset",
        action: "camera.*",
        description: "Lens, projection, smoothing, and pose reset.",
      },
      {
        name: "play / set / stop",
        action: "camera.play / camera.set / camera.stop",
        description: "Control a stored camera track player.",
      },
    ],
    returns: "Camera command result correlated to current snapshot state.",
    example: '{"operation":"aim_target","token":"Hunter"}',
    errors: [
      "EOPNOTSUPP when camera/schedule support is disabled; EBUSY when ownership context cannot be taken; EINVAL for invalid frame or values.",
    ],
    notes: [
      "KSA vectors use the documented CCI/body frames.",
      "Orthographic height cannot be restored after giving ownership back.",
    ],
  },
  "gatos.camera_track": {
    name: "gatos.camera_track",
    kind: "tool",
    category: "Camera and media",
    summary: "Manage structured JSON camera tracks and their playback.",
    useWhen: "Uploading, inspecting, deleting, or playing a cinematic camera program.",
    gate: "camera_enabled; play/update/stop also use the schedule player.",
    fields: [
      {
        name: "operation",
        type: "string",
        required: true,
        description: "list, read, upload, delete, play, update, or stop.",
      },
      {
        name: "name",
        type: "string | null",
        default: "null",
        description: "Track name for all operations except list.",
      },
      {
        name: "json",
        type: "string | null",
        default: "null",
        description: "Structured camera-track JSON chunk for upload.",
      },
      {
        name: "offset",
        type: "integer",
        default: "0",
        description: "Byte offset for an upload chunk.",
      },
      {
        name: "complete",
        type: "boolean",
        default: "true",
        description: "Finalize and validate the uploaded track.",
      },
      { name: "value", type: "number", default: "0", description: "Playback operation scalar." },
      {
        name: "token",
        type: "string | null",
        default: "null",
        description: "Fallback track/player token.",
      },
    ],
    operations: [
      { name: "list / read", description: "List stored names or return one track's JSON." },
      { name: "upload / delete", description: "Chunk-upload or remove a stored track." },
      {
        name: "play / update / stop",
        action: "camera.play / camera.set / camera.stop",
        description: "Control the camera player.",
      },
    ],
    returns: "Store metadata, track JSON, or playback command outcome.",
    example:
      '{"operation":"upload","name":"launch","offset":0,"complete":true,"json":"{\\"version\\":1,\\"shots\\":[]}"}',
    errors: [
      "ENOENT for missing tracks; EEXIST/ENOSPC/EFBIG for store constraints; EINVAL for invalid JSON or operation fields.",
    ],
    notes: ["Track time is seconds; schedule_batch offsets are milliseconds."],
  },
  "gatos.audio_control": {
    name: "gatos.audio_control",
    kind: "tool",
    category: "Camera and media",
    summary: "Control named audio playback channels.",
    useWhen: "Playing a stored clip or updating/stopping an existing channel.",
    gate: "audio_enabled.",
    fields: logicalFields,
    operations: [
      {
        name: "play",
        action: "audio.play",
        description: "Start a named clip/channel using canonical named fields.",
      },
      {
        name: "update / pause / resume / seek",
        action: "audio.set",
        description: "Update playback state, position, gain, or other channel fields.",
      },
      { name: "stop", action: "audio.stop", description: "Stop a playback channel." },
    ],
    returns: "Audio command outcome with freshness metadata.",
    example: '{"operation":"play","token":"launch.wav","aux":"mission","value":1}',
    errors: [
      "EOPNOTSUPP when audio is disabled; ENOENT for a missing clip/channel; EINVAL for invalid channel fields.",
    ],
  },
  "gatos.audio_clip": {
    name: "gatos.audio_clip",
    kind: "tool",
    category: "Camera and media",
    summary: "List, retrieve, chunk-upload, or delete stored audio clips.",
    useWhen: "Managing the clip store separately from playback channels.",
    gate: "audio_enabled and AudioStore limits.",
    fields: [
      {
        name: "operation",
        type: "string",
        required: true,
        description: "list, retrieve, upload, or delete.",
      },
      {
        name: "name",
        type: "string | null",
        default: "null",
        description: "Clip name for retrieve, upload, or delete.",
      },
      {
        name: "offset",
        type: "integer",
        default: "0",
        description: "Decoded byte offset for an upload chunk.",
      },
      {
        name: "complete",
        type: "boolean",
        default: "true",
        description: "Finalize the upload after this chunk.",
      },
      {
        name: "data_base64",
        type: "string | null",
        default: "null",
        description: "Base64-encoded bytes for upload.",
      },
    ],
    operations: [
      { name: "list", description: "List stored clips." },
      {
        name: "retrieve",
        description:
          "Return metadata and MCP AudioContentBlock for known formats, otherwise an embedded binary resource.",
      },
      { name: "upload", description: "Write a bounded base64 chunk through AudioStore." },
      { name: "delete", description: "Remove a clip." },
    ],
    returns: "Store metadata; retrieve additionally emits audio or embedded binary content.",
    example: '{"operation":"retrieve","name":"launch.wav"}',
    errors: [
      "ENOENT for a missing clip; EFBIG/ENOSPC/EEXIST/EPERM for store limits; EINVAL for malformed base64.",
    ],
    notes: [
      "Known extensions include wav, mp3, ogg/oga, flac, m4a/mp4, and aac.",
      "The HTTP request framing limit is 24 MiB; use upload chunks.",
    ],
  },
  "gatos.schedule_control": {
    name: "gatos.schedule_control",
    kind: "tool",
    category: "Scheduling",
    summary: "Inspect and control schedule and camera players.",
    useWhen:
      "Pausing, resuming, seeking, retiming, looping, stopping, or removing a timed sequence.",
    gate: "schedule_enabled; camera players additionally require camera_enabled.",
    fields: [
      {
        name: "operation",
        type: "string",
        required: true,
        description: "list, get, pause, resume, scrub, rate, loop, stop, remove, or clear.",
      },
      {
        name: "id",
        type: "string | null",
        default: "null",
        description: "Player id; omitted for list and normally clear.",
      },
      {
        name: "value",
        type: "number",
        default: "0",
        description: "Pause flag, position ms, playback rate, or loop flag as appropriate.",
      },
      {
        name: "token",
        type: "string | null",
        default: "null",
        description: "Fallback id token for control actions.",
      },
    ],
    operations: [
      { name: "list / get", description: "Read all players or one player." },
      {
        name: "pause / resume",
        action: "schedule.pause",
        description: "Set paused state; resume maps to pause value 0.",
      },
      {
        name: "scrub / rate / loop",
        action: "schedule.*",
        description: "Change position in ms, playback rate, or looping.",
      },
      {
        name: "stop / remove / clear",
        action: "schedule.*",
        description: "Stop execution or remove one/all players.",
      },
    ],
    returns: "Player state for reads or command outcome for controls.",
    example: '{"operation":"scrub","id":"launch-seq","value":3500}',
    errors: [
      "ENOENT for a missing player; EOPNOTSUPP when scheduling is disabled; EINVAL for invalid state/value.",
    ],
  },
  "gatos.debug_control": {
    name: "gatos.debug_control",
    kind: "tool",
    category: "Debug and rendering",
    summary: "Use gatOS game-manipulation, cheat, cosmetic, and IVA operations.",
    useWhen: "The user explicitly asks to manipulate the world beyond ordinary vehicle control.",
    gate: "debug_namespace, plus feature-specific gates such as IVA physics.",
    fields: [
      {
        name: "operation",
        type: "string",
        required: true,
        description: "Debug operation below or a dotted canonical action.",
      },
      {
        name: "vessel_id",
        type: "string",
        default: '""',
        description: "Raw vessel id when the operation is vessel-scoped.",
      },
      {
        name: "ordinal",
        type: "integer",
        default: "-1",
        description: "Part, subpart, weld, or entity ordinal where required.",
      },
      ...logicalFields.slice(1),
    ],
    operations: [
      {
        name: "warp / teleport / impulse",
        action: "debug.*",
        description: "Change time warp or vessel motion/position.",
      },
      {
        name: "refill_fuel / refill_battery",
        action: "debug.*",
        description: "Refill vessel resources.",
      },
      {
        name: "control_vessel / always_render_iva",
        action: "debug.*",
        description: "Change controlled vessel or force IVA rendering.",
      },
      {
        name: "weld_create / weld_here / weld_remove / weld_clear / weld_enable",
        action: "debug.weld_*",
        description: "Manage runtime welds.",
      },
      {
        name: "docking_pushoff",
        action: "debug.docking_pushoff",
        description: "Apply debug separation at a docking module.",
      },
      {
        name: "thug_life_add / clear / remove / visible / position / rotation / size / cameras",
        action: "debug.thug_life_*",
        description: "Manage the sunglasses cosmetic.",
      },
      {
        name: "iva_physics / iva_run_outside_iva / iva_clear / iva_release / iva_nudge / iva_adopt / iva_adopt_all",
        action: "debug.iva_*",
        description: "Manage free-floating IVA cabin objects.",
      },
      {
        name: "fx_spawn / fx_clear",
        action: "debug.fx_*",
        description: "Spawn or clear face particle FX.",
      },
    ],
    returns: "Canonical debug command outcome.",
    example: '{"operation":"teleport","vessel_id":"Hunter","values":[6578100,0,0,0,7784,0]}',
    errors: [
      "EACCES when debug_namespace is disabled; ENOENT for missing entities; EINVAL for unsafe or malformed values; EOPNOTSUPP when a feature is unavailable.",
    ],
    notes: [
      "Teleport and default impulse coordinates use the vessel's current-parent CCI frame.",
      "IVA must be enabled before adopting objects.",
    ],
  },
  "gatos.render_fx_control": {
    name: "gatos.render_fx_control",
    kind: "tool",
    category: "Debug and rendering",
    summary: "Edit live engine-plume, plume-trail, cloud, or terrain fields through FxCatalog.",
    useWhen: "Adjusting gatOS's runtime render editors without addressing /sim filesystem paths.",
    gate: "debug_namespace and the selected FX capability health latch.",
    fields: [
      {
        name: "family",
        type: "string",
        required: true,
        description: "engine_plume, plume_trail, clouds, or terrain.",
      },
      {
        name: "operation",
        type: "string",
        required: true,
        description: "set, reset, or plume_trail clear.",
      },
      {
        name: "entity",
        type: "string | null",
        default: "null",
        description: "Family-specific entity selector.",
      },
      {
        name: "field",
        type: "string | null",
        default: "null",
        description: "FxCatalog field name for set.",
      },
      { name: "value", type: "number", default: "0", description: "Scalar field value." },
      {
        name: "values",
        type: "number[] | null",
        default: "null",
        description: "Vector/color field value.",
      },
      {
        name: "token",
        type: "string | null",
        default: "null",
        description: "Alternate field token.",
      },
    ],
    operations: [
      {
        name: "set",
        action: "debug.{family}_set",
        description: "Validate and set one declared FxCatalog field.",
      },
      {
        name: "reset",
        action: "debug.{family}_reset",
        description: "Restore pristine session values for the selected scope.",
      },
      { name: "clear", action: "debug.plumetrail_clear", description: "Clear live plume trails." },
    ],
    returns: "FX command outcome; use get_runtime_state to inspect published editor state.",
    example:
      '{"family":"engine_plume","operation":"set","entity":"Hunter:0","field":"size","value":1.4}',
    errors: [
      "EACCES when debug is disabled; EOPNOTSUPP for a degraded reflector; EINVAL for unknown family/entity/field or invalid value.",
    ],
    notes: [
      "FX changes are session-only and reset to captured pristine values.",
      "Entity scope differs by family; discover it from runtime state and capabilities.",
    ],
  },
  "gatos.paint_control": {
    name: "gatos.paint_control",
    kind: "tool",
    category: "Debug and rendering",
    summary: "Opt in to paint rendering and color whole vessels, individual parts, templates, or EVA materials.",
    useWhen:
      "Changing vehicle or EVA appearance while preserving gatOS's explicit shader/material opt-in lifecycle.",
    gate: "control_enabled plus the corresponding parts or kittens runtime master.",
    fields: [
      {
        name: "operation",
        type: "string",
        required: true,
        description: "Paint action suffix listed below; gatOS prefixes it with paint.",
      },
      {
        name: "vessel_id",
        type: "string",
        default: '""',
        description: "Raw vessel or EVA id for vessel-, part-, and individual-kitten rules.",
      },
      {
        name: "value",
        type: "number",
        default: "0",
        description: "0 or 1 for enabled flags and clear triggers.",
      },
      {
        name: "color",
        type: "number[] | null",
        default: "null",
        description: "Three finite normalized sRGB channels [r,g,b], each in 0..1.",
      },
      {
        name: "target",
        type: "string | null",
        default: "null",
        description:
          "Blend token, raw Part.Template.Id, uint part instance_id, or semantic EVA material name, depending on operation.",
      },
    ],
    operations: [
      {
        name: "parts_enabled / kittens_enabled",
        action: "paint.parts_enabled / paint.kittens_enabled",
        description: "Install or remove the relevant runtime rendering integration transactionally.",
      },
      {
        name: "blend",
        action: "paint.blend",
        description: "Select multiply, tint, or replace through target.",
      },
      {
        name: "global_enabled / global_color / global_clear / parts_clear",
        action: "paint.global_* / paint.parts_clear",
        description: "Manage the global vehicle rule or clear every retained vehicle rule.",
      },
      {
        name: "template_enabled / template_color / template_clear",
        action: "paint.template_*",
        description: "Manage a Part.Template.Id rule named by target.",
      },
      {
        name: "vessel_enabled / vessel_color / vessel_clear",
        action: "paint.vessel_*",
        description: "Manage a live whole-vessel rule named by vessel_id.",
      },
      {
        name: "part_enabled / part_color / part_clear",
        action: "paint.part_*",
        description: "Manage one stable uint part instance_id supplied as target within vessel_id.",
      },
      {
        name: "kitten_shared_enabled / kitten_shared_color / kitten_shared_clear / kittens_clear",
        action: "paint.kitten_shared_* / paint.kittens_clear",
        description: "Manage the shared EVA default or clear every retained EVA rule.",
      },
      {
        name: "kitten_shared_material_enabled / kitten_shared_material_color / kitten_shared_material_clear",
        action: "paint.kitten_shared_material_*",
        description: "Manage one shared semantic EVA material named by target.",
      },
      {
        name: "kitten_enabled / kitten_color / kitten_clear",
        action: "paint.kitten_*",
        description: "Manage one EVA's default rule through vessel_id.",
      },
      {
        name: "kitten_material_enabled / kitten_material_color / kitten_material_clear",
        action: "paint.kitten_material_*",
        description: "Manage one semantic material on one EVA through vessel_id and target.",
      },
    ],
    returns: "Canonical paint command outcome correlated with the current snapshot.",
    example:
      '{"operation":"vessel_color","vessel_id":"Hunter","color":[0.12,0.55,0.95]}',
    errors: [
      "EACCES when control is disabled; ENOENT for a missing live vessel/part/EVA; EINVAL for an unknown operation, target, flag, blend, or color.",
      "EBUSY when another mod owns the global shader compiler prefix; EOPNOTSUPP when audited shader or material internals are incompatible.",
    ],
    notes: [
      "Set the desired rule, then enable its rule flag and runtime master; disabling a master restores stock rendering but retains rules for re-enable.",
      "Part precedence is instance > vessel > template > global > stock. EVA precedence is individual material > individual default > shared material > shared default > stock.",
      "EVA shared rules use gatOS-owned clones; they do not overwrite KSA's shared stock MaterialData.",
    ],
  },
  "gatos.command": {
    name: "gatos.command",
    kind: "tool",
    category: "Advanced commands",
    summary: "Submit any canonical action through the complete command envelope.",
    useWhen:
      "A logical family does not express the action clearly enough, or generic catalog-driven automation is desired.",
    gate: "The canonical action's own control/debug/feature gates.",
    fields: commandFields,
    returns:
      "Validated command outcome including canonical action context and current freshness metadata.",
    example:
      '{"action":"debug.teleport","vessel_id":"Hunter","ordinal":-1,"values":[6578100,0,0,0,7784,0]}',
    errors: [
      "EINVAL for an unknown action or invalid shape; action-specific ENOENT/EACCES/EBUSY/EOPNOTSUPP/EIO.",
    ],
    notes: [
      "Call get_capabilities for units, addressing, phase, and safety metadata.",
      "Clients never choose Frame or Solver phase; the catalog derives it.",
    ],
  },
  "gatos.execute_batch": {
    name: "gatos.execute_batch",
    kind: "tool",
    category: "Advanced commands",
    summary: "Execute an ordered, atomic-admission, same-phase command group on one game tick.",
    useWhen:
      "Several state changes must reach the game together rather than across separate request/frame boundaries.",
    gate: "Every member's action gates apply.",
    fields: [
      {
        name: "commands",
        type: "command[]",
        required: true,
        description:
          "One through 64 canonical command envelopes; all actions must derive the same Frame or Solver phase.",
      },
    ],
    returns:
      "Accepted count, derived phase, overall result, and first failure index/action when execution fails.",
    example:
      '{"commands":[{"action":"vessel.throttle","vessel_id":"Hunter","value":1},{"action":"vessel.ignite","vessel_id":"Hunter","value":1}]}',
    errors: [
      "EINVAL for empty/over-64 batches, any invalid member, or mixed phases; action-specific failure after admission.",
    ],
    notes: [
      "The full array is validated before enqueue and submitted once.",
      "After a game-side failure, later commands still execute; the result reports the first failure.",
    ],
  },
  "gatos.schedule_batch": {
    name: "gatos.schedule_batch",
    kind: "tool",
    category: "Scheduling",
    summary: "Create a typed timed command sequence on render, wall, or UT time.",
    useWhen:
      "Commands need absolute offsets, mixed phases, looping, catch-up, or lifecycle controls.",
    gate: "schedule_enabled plus each entry's action gates at execution time.",
    fields: [
      {
        name: "entries",
        type: "schedule_entry[]",
        required: true,
        description: "Entries shaped as {at_ms, command}; equal offsets preserve authored order.",
      },
      {
        name: "id",
        type: "string | null",
        default: "generated",
        description: "Optional player id, reserved only after complete validation.",
      },
      {
        name: "group",
        type: "string | null",
        default: "null",
        description: "Optional schedule group.",
      },
      {
        name: "clock",
        type: '"render" | "wall" | "ut"',
        default: '"wall"',
        description: "Existing playback clock base.",
      },
      { name: "rate", type: "number", default: "1", description: "Initial playback rate." },
      {
        name: "loop",
        type: "boolean",
        default: "false",
        description: "Loop when the duration is reached.",
      },
    ],
    returns: "Assigned id, entry count, clock, rate, and loop state immediately after submission.",
    example:
      '{"id":"launch-seq","group":"launch","clock":"wall","entries":[{"at_ms":0,"command":{"action":"vessel.throttle","vessel_id":"Hunter","value":1}},{"at_ms":1200,"command":{"action":"vessel.ignite","vessel_id":"Hunter","value":1}}]}',
    errors: [
      "EOPNOTSUPP when schedules are disabled; EINVAL for clock, id/group, rate, offsets, actions, or shared entry/byte/live limits.",
    ],
    notes: [
      "Mixed Frame/Solver phases are allowed because entries execute at their own times.",
      "Catch-up preserves triggers and coalesces state controls by canonical action/vessel/ordinal key.",
      "Offsets and player positions are milliseconds.",
    ],
  },

  "gatos://world": {
    name: "gatos://world",
    kind: "resource",
    category: "Fixed resources",
    summary: "Current simulation summary as application/json.",
    useWhen: "A client prefers resource reads for the current world overview.",
    gate: "Same telemetry behavior as gatos.get_world summary.",
    returns: "The same summary presenter used by gatos.get_world with freshness metadata.",
    example: "gatos://world",
    equivalent: "gatos.get_world",
    notes: ["Live resource; no positive cache lifetime or subscriptions are advertised."],
  },
  "gatos://celestials": {
    name: "gatos://celestials",
    kind: "resource",
    category: "Fixed resources",
    summary: "Current celestial summary collection as application/json.",
    useWhen: "Browsing current bodies through MCP resources.",
    gate: "Body telemetry must be enabled.",
    returns: "Celestial summaries in deterministic order with snapshot_sequence and ut.",
    example: "gatos://celestials",
    equivalent: "gatos.list_celestials",
    notes: [
      "Resource reads return the collection directly; use the list tool for explicit pagination control.",
    ],
  },
  "gatos://celestials/{id}": {
    name: "gatos://celestials/{id}",
    kind: "resource",
    category: "Resource templates",
    summary: "One complete celestial by raw id.",
    useWhen: "Reading a known body through resources/read.",
    gate: "Body telemetry must be enabled.",
    fields: [
      {
        name: "id",
        type: "URI template variable",
        required: true,
        description: "Raw body id, URI-encoded by the client.",
      },
    ],
    returns: "Complete BodySnapshot with freshness metadata.",
    example: "gatos://celestials/Kerbin",
    equivalent: "gatos.get_celestial",
    errors: ["ENOENT data when the current snapshot has no matching body."],
  },
  "gatos://vessels": {
    name: "gatos://vessels",
    kind: "resource",
    category: "Fixed resources",
    summary: "Current vessel summary collection as application/json.",
    useWhen: "Browsing all current vessels through MCP resources.",
    gate: "Vessel telemetry must be enabled.",
    returns: "Vessel summaries in stable order with snapshot_sequence and ut.",
    example: "gatos://vessels",
    equivalent: "gatos.list_vessels",
    notes: ["Use gatos.list_vessels when filters or page size matter."],
  },
  "gatos://vessels/{id}": {
    name: "gatos://vessels/{id}",
    kind: "resource",
    category: "Resource templates",
    summary: "One complete vessel by raw id.",
    useWhen: "Reading the complete known vessel document through resources/read.",
    gate: "Vessel telemetry detail gates apply.",
    fields: [
      {
        name: "id",
        type: "URI template variable",
        required: true,
        description: 'Raw vessel id or "active", URI-encoded by the client.',
      },
    ],
    returns: "Complete vessel document with all logical sections and freshness metadata.",
    example: "gatos://vessels/Hunter",
    equivalent: "gatos.get_vessel",
    notes: [
      "There is no JSON result truncation; use the tool when selecting sections is preferable.",
    ],
  },
  "gatos://kittens": {
    name: "gatos://kittens",
    kind: "resource",
    category: "Fixed resources",
    summary: "Current EVA kitten summary collection.",
    useWhen: "Browsing only kitten vessels through MCP resources.",
    gate: "Vessel telemetry must be enabled.",
    returns: "The vessel collection filtered by is_kitten, with freshness metadata.",
    example: "gatos://kittens",
    equivalent: "gatos.list_kittens",
  },
  "gatos://kittens/{id}": {
    name: "gatos://kittens/{id}",
    kind: "resource",
    category: "Resource templates",
    summary: "One complete kitten vessel by raw id.",
    useWhen: "Reading a known kitten as a complete vessel document.",
    gate: "Vessel telemetry must be enabled.",
    fields: [
      {
        name: "id",
        type: "URI template variable",
        required: true,
        description: "Raw kitten vessel id, URI-encoded by the client.",
      },
    ],
    returns: "Complete vessel document after kitten validation, with freshness metadata.",
    example: "gatos://kittens/Valentina",
    equivalent: "gatos.get_kitten",
    errors: ["ENOENT/EINVAL data for missing or non-kitten ids."],
  },
  "gatos://runtime/{feature}": {
    name: "gatos://runtime/{feature}",
    kind: "resource",
    category: "Resource templates",
    summary: "Complete published runtime state for one feature.",
    useWhen:
      "Inspecting camera, schedule, audio, paint, cosmetic, IVA, or render-FX state through resources.",
    gate: "The selected feature's gate and store availability.",
    fields: [
      {
        name: "feature",
        type: "URI template variable",
        required: true,
        description:
          "camera, schedules, audio, paint, welds, thug_life, face_fx, iva, engine_plume, plume_trail, clouds, or terrain.",
      },
    ],
    returns: "Complete store/runtime state correlated with the current simulation sequence.",
    example: "gatos://runtime/paint",
    equivalent: "gatos.get_runtime_state",
    errors: ["EINVAL/EOPNOTSUPP data for unknown or disabled features."],
  },
  "gatos://capabilities": {
    name: "gatos://capabilities",
    kind: "resource",
    category: "Fixed resources",
    summary: "Current command and feature capability catalog.",
    useWhen: "A resource-oriented client needs machine-readable discovery.",
    gate: "Always available while MCP is running.",
    returns:
      "Operations, action keys, schemas, units, phases, gates, availability, limits, and safety notes.",
    example: "gatos://capabilities",
    equivalent: "gatos.get_capabilities",
    notes: ["Generated from the same command catalog used for validation."],
  },
};
