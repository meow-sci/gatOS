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
  callShape?: string;
  example?: string;
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
    summary:
      "Discover canonical actions, argument shapes, phases, gates, list limits, and safety metadata.",
    useWhen:
      "At session start, before using optional features, or whenever an operation's units or availability are uncertain.",
    gate: "Always available while MCP is running.",
    fields: [],
    returns:
      "A catalog-generated document with list limits, feature availability, authority state, and per-action metadata.",
    example: "{}",
    equivalent: "gatos://capabilities",
    notes: [
      "Treat this as the machine-readable authority for action metadata. Read world status for transport/accessor health and runtime state for feature-store limits.",
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
        name: "ignite | shutdown | stage",
        action: "vessel.ignite | vessel.shutdown | vessel.stage",
        callShape: "{ operation, vessel_id }",
        example: '{"operation":"stage","vessel_id":"Hunter"}',
        description:
          "One-shot engine or staging triggers. Omitted payload fields keep their defaults and are ignored.",
      },
      {
        name: "engine_master | lights | rcs | always_render",
        action: "vessel.engine | vessel.lights | vessel.rcs | vessel.always_render",
        callShape: "{ operation, vessel_id, value: 0 | 1 }",
        example: '{"operation":"rcs","vessel_id":"Hunter","value":1}',
        description: "Set a vessel-wide flag. engine_master maps to vessel.engine.",
      },
      {
        name: "throttle",
        action: "vessel.throttle",
        callShape: '{ operation: "throttle", vessel_id, value: number  // 0..1 }',
        example: '{"operation":"throttle","vessel_id":"Hunter","value":0.72}',
        description: "Set manual throttle as a normalized fraction.",
      },
      {
        name: "translate | rotate",
        action: "vessel.translate | vessel.rotate",
        callShape: "{ operation, vessel_id, values: [x, y, z]  // signs; magnitudes ignored }",
        example: '{"operation":"translate","vessel_id":"Hunter","values":[1,0,-1]}',
        description:
          "Latch body-axis bang-bang RCS commands. Write [0,0,0] to stop; rcs_mode must be Enabled.",
      },
      {
        name: "rcs_mode",
        action: "vessel.rcs_mode",
        callShape: '{ operation: "rcs_mode", vessel_id, token: "Enabled" | "Disabled" }',
        example: '{"operation":"rcs_mode","vessel_id":"Hunter","token":"Enabled"}',
        description:
          "Set the flight computer's RCS master switch. This is distinct from the vessel rcs flag.",
      },
      {
        name: "attitude_mode",
        action: "vessel.attitude_mode",
        callShape:
          '{ operation: "attitude_mode", vessel_id, token: "manual" | "StabilityAssist" | "Prograde" | "Retrograde" | "Normal" | "AntiNormal" | "RadialIn" | "RadialOut" | "Target" | "AntiTarget" | "Maneuver" }',
        example: '{"operation":"attitude_mode","vessel_id":"Hunter","token":"Prograde"}',
        description:
          "Choose manual control or a named flight-computer tracking mode. Matching is case-insensitive.",
      },
      {
        name: "attitude_frame",
        action: "vessel.attitude_frame",
        callShape:
          '{ operation: "attitude_frame", vessel_id, token: "Inertial" | "Orbital" | "Surface" | "Target" }',
        example: '{"operation":"attitude_frame","vessel_id":"Hunter","token":"Orbital"}',
        description: "Set the reference frame used by named attitude modes.",
      },
      {
        name: "attitude_target",
        action: "vessel.attitude_target",
        callShape: '{ operation: "attitude_target", vessel_id, values: [x, y, z, w] }',
        example: '{"operation":"attitude_target","vessel_id":"Hunter","values":[0,0,0,1]}',
        description:
          "Set a Body-to-CCI quaternion. The quaternion norm must be valid for the game actuator.",
      },
      {
        name: "burn",
        action: "vessel.burn",
        callShape: '{ operation: "burn", vessel_id, values: [ut_seconds, dv_x, dv_y, dv_z] }',
        example: '{"operation":"burn","vessel_id":"Hunter","values":[12840,0,12.5,0]}',
        description:
          "Set a flight-computer burn at absolute UT with a parent-body CCI delta-v in m/s.",
      },
      {
        name: "scale",
        action: "vessel.scale",
        callShape: '{ operation: "scale", vessel_id, value: number  // finite and > 0 }',
        example: '{"operation":"scale","vessel_id":"Hunter","value":25}',
        description: "Set session render scale. Value 1 restores ordinary scale.",
      },
      {
        name: "focus | take_control",
        action: "camera.focus | debug.control_vessel",
        callShape: "{ operation, vessel_id }",
        example: '{"operation":"focus","vessel_id":"Hunter"}',
        description:
          "Focus is view-only; take_control changes the player-controlled vessel and is a debug action.",
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
        name: "engine_active | rcs_active | light_on",
        action: "engine.active | rcs.active | light.on",
        callShape: "{ operation, vessel_id, ordinal, value: 0 | 1 }",
        example: '{"operation":"engine_active","vessel_id":"Hunter","ordinal":0,"value":1}',
        description: "Enable or disable one indexed engine, RCS controller, or light.",
      },
      {
        name: "engine_minimum_throttle",
        action: "engine.min_throttle",
        callShape:
          '{ operation: "engine_minimum_throttle", vessel_id, ordinal, value: number  // 0..1 }',
        example:
          '{"operation":"engine_minimum_throttle","vessel_id":"Hunter","ordinal":0,"value":0.15}',
        description:
          "Set one engine's normalized minimum-throttle fraction. engine_min_throttle is an alias.",
      },
      {
        name: "light_brightness",
        action: "light.brightness",
        callShape: '{ operation: "light_brightness", vessel_id, ordinal, value: number  // >= 0 }',
        example: '{"operation":"light_brightness","vessel_id":"Hunter","ordinal":2,"value":4}',
        description: "Set one light's brightness multiplier.",
      },
      {
        name: "light_color",
        action: "light.color",
        callShape:
          '{ operation: "light_color", vessel_id, ordinal, values: [r, g, b]  // each 0..1 }',
        example:
          '{"operation":"light_color","vessel_id":"Hunter","ordinal":2,"values":[1,0.25,0.05]}',
        description: "Set one light's normalized RGB color.",
      },
      {
        name: "light_outer_angle | light_inner_angle",
        action: "light.outer_angle | light.inner_angle",
        callShape: "{ operation, vessel_id, ordinal, value: degrees }",
        example: '{"operation":"light_outer_angle","vessel_id":"Hunter","ordinal":2,"value":42}',
        description: "Set spotlight cone angles; the inner angle cannot exceed the outer angle.",
      },
      {
        name: "animation_goal | solar_deployment",
        action: "animation.goal",
        callShape: "{ operation, vessel_id, ordinal, value: number  // 0..1 }",
        example: '{"operation":"solar_deployment","vessel_id":"Hunter","ordinal":0,"value":1}',
        description: "Drive an animation or solar deployment to a normalized goal.",
      },
      {
        name: "undock | fire_decoupler",
        action: "docking.undock | decoupler.fire",
        callShape: "{ operation, vessel_id, ordinal }",
        example: '{"operation":"undock","vessel_id":"Hunter","ordinal":0}',
        description:
          "Fire a one-shot indexed separation trigger. Inspect live module state before retrying.",
      },
      {
        name: "pushoff",
        action: "debug.docking_pushoff",
        callShape: '{ operation: "pushoff", vessel_id, ordinal, value: impulse_newton_seconds }',
        example: '{"operation":"pushoff","vessel_id":"Hunter","ordinal":0,"value":250}',
        description:
          "Set a docking port's debug undock push-off impulse. Requires debug_namespace.",
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
        name: "ownership | take",
        action: "camera.enabled",
        callShape: "{ operation, value: 0 | 1 }",
        example: '{"operation":"ownership","value":1}',
        description: "Take ownership with 1 or perform the configured eased hand-back with 0.",
      },
      {
        name: "release | reset | stop",
        action: "camera.release | camera.pose_reset | camera.stop",
        callShape: "{ operation }",
        example: '{"operation":"release"}',
        description: "One-shot hard release, pose-override reset, or camera-player stop.",
      },
      {
        name: "mode",
        action: "camera.mode",
        callShape: '{ operation: "mode", token: "orbit" | "free" | "map" | "iva" | "fixed" }',
        example: '{"operation":"mode","token":"fixed"}',
        description: "Change the game's camera mode while gatOS does not own the camera.",
      },
      {
        name: "follow | anchor | aim_target",
        action: "camera.follow | camera.anchor | camera.aim_target",
        callShape:
          '{ operation, token: "vessel:<id>" | "body:<id>" | "part:<vessel>/<instance_id>" | "none" }',
        example: '{"operation":"anchor","token":"vessel:Hunter"}',
        description: "Set a target reference. follow does not accept part targets; anchor does.",
      },
      {
        name: "tidal | ortho",
        action: "camera.tidal | camera.ortho",
        callShape: "{ operation, value: 0 | 1 }",
        example: '{"operation":"ortho","value":1}',
        description: "Set a boolean game-camera or projection channel.",
      },
      {
        name: "map_scope | orbit_radius | orbit_azimuth | orbit_elevation | roll | fov | ortho_height | smoothing",
        action: "camera.*",
        callShape: "{ operation, value: number }",
        example: '{"operation":"smoothing","value":0.35}',
        description:
          "Set one scalar. Units/ranges: map_scope and radii/heights are metres; azimuth/elevation/roll/fov are degrees; smoothing is 0..10 seconds.",
      },
      {
        name: "position | aim_offset",
        action: "camera.position | camera.aim_offset",
        callShape: "{ operation, values: [x, y, z], token?: frame }",
        example: '{"operation":"position","values":[-40,0,-6],"token":"bodyfixed"}',
        description:
          "Set a three-vector. position may carry a placement-frame token; aim_offset uses the current aim frame.",
      },
      {
        name: "frame | aim_frame",
        action: "camera.frame | camera.aim_frame",
        callShape: '{ operation, token: "ecl" | "cce" | "bodyfixed" | "enu" | "lvlh" | "chase" }',
        example: '{"operation":"frame","token":"bodyfixed"}',
        description: "Select the placement or aim-offset reference frame.",
      },
      {
        name: "geodetic",
        action: "camera.geo",
        callShape:
          '{ operation: "geodetic", values: [lat_deg, lon_deg, altitude_m], token?: "body:<id>" }',
        example: '{"operation":"geodetic","values":[12.5,-42,800],"token":"body:Kerbin"}',
        description: "Place over terrain on an explicit body or the current body anchor.",
      },
      {
        name: "rotation",
        action: "camera.rotation",
        callShape: '{ operation: "rotation", values: [x, y, z, w] }',
        example: '{"operation":"rotation","values":[0,0,0,1]}',
        description: "Set an explicit camera quaternion; valid norm is 0.5..2.",
      },
      {
        name: "aim",
        action: "camera.aim",
        callShape:
          '{ operation: "aim", token: target_ref, values: [off_x, off_y, off_z, frame_ordinal, up_ordinal, roll_deg, roll_present] }',
        example: '{"operation":"aim","token":"vessel:Hunter","values":[0,0,-1.2,2,0,0,0]}',
        description:
          "Set the complete aim constraint. Frame ordinals 0..5 are ecl/cce/bodyfixed/enu/lvlh/chase; up ordinals 0..3 are world/target/velocity/free.",
      },
      {
        name: "aim_up",
        action: "camera.aim_up",
        callShape: '{ operation: "aim_up", token: "world" | "target" | "velocity" | "free" }',
        example: '{"operation":"aim_up","token":"world"}',
        description: "Choose the camera aim up-reference.",
      },
      {
        name: "play",
        action: "camera.play",
        callShape:
          '{ operation: "play", token: track_name, aux?: group, values?: [at_s, rate, loop, at_present, rate_present, loop_present] }',
        example: '{"operation":"play","token":"launch","values":[0,1,0,0,0,0]}',
        description:
          "Start a stored track. Track time is seconds and schedule_enabled is also required.",
      },
      {
        name: "set",
        action: "camera.set",
        callShape:
          '{ operation: "set", values: [key, value, ...]  // 0=t_s, 1=rate, 2=loop, 3=paused }',
        example: '{"operation":"set","values":[3,1]}',
        description: "Patch the active camera player with flat numeric key/value pairs.",
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
      {
        name: "list",
        callShape: '{ operation: "list" }',
        example: '{"operation":"list"}',
        description: "List stored track names and metadata.",
      },
      {
        name: "read",
        callShape: '{ operation: "read", name }',
        example: '{"operation":"read","name":"launch"}',
        description: "Return one track's complete JSON.",
      },
      {
        name: "upload",
        callShape: '{ operation: "upload", name, json, offset?: byte_offset, complete?: boolean }',
        example:
          '{"operation":"upload","name":"launch","json":"{\\"version\\":1,\\"shots\\":[]}","offset":0,"complete":true}',
        description: "Write a UTF-8 JSON chunk; complete=true validates and commits the track.",
      },
      {
        name: "delete",
        callShape: '{ operation: "delete", name }',
        example: '{"operation":"delete","name":"launch"}',
        description: "Remove one stored track.",
      },
      {
        name: "play | update | stop",
        action: "camera.play / camera.set / camera.stop",
        callShape: "{ operation, name?, value?, token? }",
        example: '{"operation":"play","name":"launch"}',
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
        callShape:
          '{ operation: "play", token: clip_name, aux?: channel_id, values?: [start_ms, end_ms, volume, loop, pan, pitch, group] }',
        example:
          '{"operation":"play","token":"launch.wav","aux":"mission","values":[0,0,1,0,0,1,0]}',
        description:
          "Start a clip. group is 0=sfx, 1=music, or 2=ui; end_ms 0 means the whole clip.",
      },
      {
        name: "update / pause / resume / seek",
        action: "audio.set",
        callShape:
          "{ operation, token: channel_id_or_clip, values: [key, value, ...]  // 0=volume, 1=pan, 2=pitch, 3=paused, 4=seek_ms }",
        example: '{"operation":"update","token":"mission","values":[0,0.25,3,1]}',
        description:
          "Patch a live channel using flat numeric key/value pairs. pause/resume/seek map to this same action.",
      },
      {
        name: "stop",
        action: "audio.stop",
        callShape: '{ operation: "stop", token: "all" | channel_id | clip_name }',
        example: '{"operation":"stop","token":"mission"}',
        description: "Stop one matching channel/clip, or all channels.",
      },
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
      {
        name: "list",
        callShape: '{ operation: "list" }',
        example: '{"operation":"list"}',
        description: "List stored clips.",
      },
      {
        name: "retrieve",
        callShape: '{ operation: "retrieve", name }',
        example: '{"operation":"retrieve","name":"launch.wav"}',
        description:
          "Return metadata and MCP AudioContentBlock for known formats, otherwise an embedded binary resource.",
      },
      {
        name: "upload",
        callShape:
          '{ operation: "upload", name, data_base64, offset?: decoded_byte_offset, complete?: boolean }',
        example:
          '{"operation":"upload","name":"beep.wav","data_base64":"UklGRg==","offset":0,"complete":true}',
        description: "Write a bounded base64 chunk through AudioStore.",
      },
      {
        name: "delete",
        callShape: '{ operation: "delete", name }',
        example: '{"operation":"delete","name":"beep.wav"}',
        description: "Remove a clip.",
      },
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
      {
        name: "list",
        callShape: '{ operation: "list" }',
        example: '{"operation":"list"}',
        description: "Read all players.",
      },
      {
        name: "get",
        callShape: '{ operation: "get", id }',
        example: '{"operation":"get","id":"launch-seq"}',
        description: "Read one player.",
      },
      {
        name: "pause / resume",
        action: "schedule.pause",
        callShape: "{ operation, id, value?: 0 | 1 }",
        example: '{"operation":"pause","id":"launch-seq","value":1}',
        description: "Set paused state; resume maps to pause value 0 automatically.",
      },
      {
        name: "scrub",
        action: "schedule.scrub",
        callShape: '{ operation: "scrub", id, value: position_ms }',
        example: '{"operation":"scrub","id":"launch-seq","value":3500}',
        description: "Seek without firing skipped entries.",
      },
      {
        name: "rate",
        action: "schedule.rate",
        callShape: '{ operation: "rate", id, value: number  // 0..100 }',
        example: '{"operation":"rate","id":"launch-seq","value":0.5}',
        description: "Set playback rate; 0 is a legal frozen state.",
      },
      {
        name: "loop",
        action: "schedule.*",
        callShape: '{ operation: "loop", id, value: 0 | 1 }',
        example: '{"operation":"loop","id":"launch-seq","value":1}',
        description: "Enable or disable looping.",
      },
      {
        name: "stop | remove",
        action: "schedule.*",
        callShape: "{ operation, id }",
        example: '{"operation":"stop","id":"launch-seq"}',
        description: "Stop execution while keeping the player, or remove it from the registry.",
      },
      {
        name: "clear",
        action: "schedule.clear",
        callShape: '{ operation: "clear" }',
        example: '{"operation":"clear"}',
        description: "Remove every live and committed-but-not-activated player.",
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
        name: "warp",
        action: "debug.warp",
        callShape: '{ operation: "warp", value: multiplier }',
        example: '{"operation":"warp","value":10}',
        description: "Set the game time-warp factor.",
      },
      {
        name: "teleport",
        action: "debug.teleport",
        callShape:
          '{ operation: "teleport", vessel_id, values: [px_m, py_m, pz_m, vx_mps, vy_mps, vz_mps] }',
        example: '{"operation":"teleport","vessel_id":"Hunter","values":[6578100,0,0,0,7784,0]}',
        description:
          "Set a CCI state vector about the vessel's current parent body; this does not change SOI.",
      },
      {
        name: "impulse",
        action: "debug.impulse",
        callShape:
          '{ operation: "impulse", vessel_id, values: [x, y, z], token?: "cci" | "body", aux?: "ns" | "dv" }',
        example:
          '{"operation":"impulse","vessel_id":"Hunter","values":[10,0,0],"token":"body","aux":"dv"}',
        description:
          'Apply an impulse; defaults are parent-body CCI and newton-seconds. aux="dv" changes the vector to m/s delta-v.',
      },
      {
        name: "refill_fuel | refill_battery",
        action: "debug.refill_fuel | debug.refill_battery",
        callShape: "{ operation, vessel_id }",
        example: '{"operation":"refill_fuel","vessel_id":"Hunter"}',
        description: "Refill one vessel's relevant resources on the solver phase.",
      },
      {
        name: "control_vessel | always_render_iva",
        action: "debug.control_vessel | debug.always_render_iva",
        callShape:
          '{ operation: "control_vessel", vessel_id } OR { operation: "always_render_iva", value: 0 | 1 }',
        example: '{"operation":"control_vessel","vessel_id":"Hunter"}',
        description: "Change the player-controlled vessel or globally force IVA meshes to render.",
      },
      {
        name: "weld_create",
        action: "debug.weld_create",
        callShape:
          '{ operation: "weld_create", vessel_id: source, token: target, values: [part_iid, x, y, z, pitch, yaw, roll, lock] }',
        example:
          '{"operation":"weld_create","vessel_id":"Tug","token":"Station","values":[42,0,0,0,0,0,0,1]}',
        description:
          "Create an explicit runtime weld from a source vessel to a target part/subpart.",
      },
      {
        name: "weld_here",
        action: "debug.weld_here",
        callShape:
          '{ operation: "weld_here", vessel_id: source, token: target, values: [part_iid, lock] }',
        example: '{"operation":"weld_here","vessel_id":"Tug","token":"Station","values":[42,1]}',
        description: "Capture the current relative pose and weld there.",
      },
      {
        name: "weld_remove | weld_clear",
        action: "debug.weld_remove | debug.weld_clear",
        callShape: '{ operation: "weld_remove", vessel_id: source } OR { operation: "weld_clear" }',
        example: '{"operation":"weld_remove","vessel_id":"Tug"}',
        description: "Remove one source weld or every weld.",
      },
      {
        name: "weld_enable",
        action: "debug.weld_enable",
        callShape: '{ operation: "weld_enable", vessel_id: source, value: 0 | 1 }',
        example: '{"operation":"weld_enable","vessel_id":"Tug","value":0}',
        description: "Suspend or resume a retained weld.",
      },
      {
        name: "docking_pushoff",
        action: "debug.docking_pushoff",
        callShape:
          '{ operation: "docking_pushoff", vessel_id, ordinal: docking_port_index, value: impulse_newton_seconds }',
        example: '{"operation":"docking_pushoff","vessel_id":"Hunter","ordinal":0,"value":250}',
        description: "Set debug separation impulse for an indexed docking port.",
      },
      {
        name: "thug_life_add",
        action: "debug.thug_life_add",
        callShape:
          '{ operation: "thug_life_add", token: vessel_id, values: [part_iid] | [part_iid, x, y, z, pitch, yaw, roll, width, height] }',
        example: '{"operation":"thug_life_add","token":"Hunter","values":[42]}',
        description: "Create a part-anchored sunglasses render entry.",
      },
      {
        name: "thug_life_remove | visible | position | rotation | size | cameras",
        action: "debug.thug_life_*",
        callShape: "{ operation, ordinal: entry_id, value? | values? | token? }",
        example: '{"operation":"thug_life_visible","ordinal":0,"value":1}',
        description:
          "Mutate one entry: flag in value, position/rotation/size in values, or camera mask in token. thug_life_clear needs only operation.",
      },
      {
        name: "iva_physics | iva_run_outside_iva",
        action: "debug.iva_physics | debug.iva_run_outside_iva",
        callShape: "{ operation, value: 0 | 1 }",
        example: '{"operation":"iva_physics","value":1}',
        description: "Enable cabin physics or its outside-IVA simulation policy.",
      },
      {
        name: "iva_adopt",
        action: "debug.iva_adopt",
        callShape:
          '{ operation: "iva_adopt", token: vessel_id, values: [subpart_iid] | [subpart_iid, vx, vy, vz] }',
        example: '{"operation":"iva_adopt","token":"Hunter","values":[4123]}',
        description: "Adopt an interior SubPart, optionally with assembly-frame starting velocity.",
      },
      {
        name: "iva_adopt_all",
        action: "debug.iva_adopt_all",
        callShape:
          '{ operation: "iva_adopt_all", token: vessel_id, value?: max_count, aux?: template_substring }',
        example: '{"operation":"iva_adopt_all","token":"Hunter","value":4,"aux":"Sardine"}',
        description:
          "Adopt the smallest eligible props, optionally filtered by template substring.",
      },
      {
        name: "iva_nudge | iva_release | iva_clear",
        action: "debug.iva_*",
        callShape:
          '{ operation: "iva_nudge", ordinal: object_id, values: [vx, vy, vz] } OR { operation: "iva_release", ordinal: object_id } OR { operation: "iva_clear" }',
        example: '{"operation":"iva_nudge","ordinal":0,"values":[0.3,0,0]}',
        description: "Kick/release one floating object, or return every object to rest.",
      },
      {
        name: "fx_spawn | fx_clear",
        action: "debug.fx_spawn | debug.fx_clear",
        callShape:
          '{ operation: "fx_spawn", token: vessel_id, aux: profile, values?: [scale, off_x, off_y, off_z] } OR { operation: "fx_clear" }',
        example: '{"operation":"fx_spawn","token":"Valentina","aux":"sparkle","values":[1,0,0,0]}',
        description: "Spawn a face/vehicle-anchored particle burst or stop all gatOS effects.",
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
        callShape: '{ family, operation: "set", entity, field, value?: scalar, values?: vector }',
        example:
          '{"family":"engine_plume","operation":"set","entity":"MethaloxVac","field":"emission/brightness","value":35}',
        description:
          "Validate and set one declared FxCatalog field. engine_plume entity is a template id; plume_trail entity is omitted; clouds entity is a body id; terrain uses a body id or an empty entity for wireframe.",
      },
      {
        name: "reset",
        action: "debug.{family}_reset",
        callShape: '{ family, operation: "reset", entity? }',
        example: '{"family":"clouds","operation":"reset","entity":"Kerbin"}',
        description:
          "Restore pristine session values for a template, body, or the global plume-trail renderer.",
      },
      {
        name: "clear",
        action: "debug.plumetrail_clear",
        callShape: '{ family: "plume_trail", operation: "clear" }',
        example: '{"family":"plume_trail","operation":"clear"}',
        description: "Drop existing live trail geometry without changing renderer settings.",
      },
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
    summary:
      "Opt in to paint rendering and color whole vessels, individual parts, templates, or EVA materials.",
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
        callShape: "{ operation, value: 0 | 1 }",
        example: '{"operation":"parts_enabled","value":1}',
        description:
          "Install or remove the relevant runtime rendering integration transactionally.",
      },
      {
        name: "blend",
        action: "paint.blend",
        callShape: '{ operation: "blend", target: "multiply" | "tint" | "replace" }',
        example: '{"operation":"blend","target":"tint"}',
        description: "Select the vehicle shader blend mode through target.",
      },
      {
        name: "global_enabled | global_color | global_clear | parts_clear",
        action: "paint.global_* / paint.parts_clear",
        callShape:
          '{ operation, value: 0 | 1 } for enabled/clear; { operation: "global_color", color: [r,g,b] } for color',
        example: '{"operation":"global_color","color":[0.12,0.55,0.95]}',
        description:
          "Manage the global vehicle rule or clear every retained vehicle rule. RGB channels are normalized 0..1.",
      },
      {
        name: "template_enabled / template_color / template_clear",
        action: "paint.template_*",
        callShape: "{ operation, target: part_template_id, value?: 0 | 1, color?: [r,g,b] }",
        example: '{"operation":"template_color","target":"FuelTankSmall","color":[0.8,0.2,0.1]}',
        description:
          "Manage a Part.Template.Id rule named by target; color operations use color and enabled/clear use value.",
      },
      {
        name: "vessel_enabled / vessel_color / vessel_clear",
        action: "paint.vessel_*",
        callShape: "{ operation, vessel_id, value?: 0 | 1, color?: [r,g,b] }",
        example: '{"operation":"vessel_color","vessel_id":"Hunter","color":[0.12,0.55,0.95]}',
        description: "Manage a live whole-vessel rule named by vessel_id.",
      },
      {
        name: "part_enabled / part_color / part_clear",
        action: "paint.part_*",
        callShape:
          "{ operation, vessel_id, target: part_instance_id_as_string, value?: 0 | 1, color?: [r,g,b] }",
        example:
          '{"operation":"part_color","vessel_id":"Hunter","target":"4123","color":[1,0.5,0]}',
        description: "Manage one stable uint part instance_id supplied as target within vessel_id.",
      },
      {
        name: "kitten_shared_enabled / kitten_shared_color / kitten_shared_clear / kittens_clear",
        action: "paint.kitten_shared_* / paint.kittens_clear",
        callShape: "{ operation, value?: 0 | 1, color?: [r,g,b] }",
        example: '{"operation":"kitten_shared_color","color":[0.7,0.7,1]}',
        description: "Manage the shared EVA default or clear every retained EVA rule.",
      },
      {
        name: "kitten_shared_material_enabled / kitten_shared_material_color / kitten_shared_material_clear",
        action: "paint.kitten_shared_material_*",
        callShape: "{ operation, target: semantic_material_name, value?: 0 | 1, color?: [r,g,b] }",
        example:
          '{"operation":"kitten_shared_material_color","target":"visor","color":[0.1,0.3,0.8]}',
        description: "Manage one shared semantic EVA material named by target.",
      },
      {
        name: "kitten_enabled / kitten_color / kitten_clear",
        action: "paint.kitten_*",
        callShape: "{ operation, vessel_id: eva_id, value?: 0 | 1, color?: [r,g,b] }",
        example: '{"operation":"kitten_color","vessel_id":"Valentina","color":[1,0.2,0.4]}',
        description: "Manage one EVA's default rule through vessel_id.",
      },
      {
        name: "kitten_material_enabled / kitten_material_color / kitten_material_clear",
        action: "paint.kitten_material_*",
        callShape:
          "{ operation, vessel_id: eva_id, target: semantic_material_name, value?: 0 | 1, color?: [r,g,b] }",
        example:
          '{"operation":"kitten_material_color","vessel_id":"Valentina","target":"visor","color":[0.1,0.3,0.8]}',
        description: "Manage one semantic material on one EVA through vessel_id and target.",
      },
    ],
    returns: "Canonical paint command outcome correlated with the current snapshot.",
    example: '{"operation":"vessel_color","vessel_id":"Hunter","color":[0.12,0.55,0.95]}',
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
