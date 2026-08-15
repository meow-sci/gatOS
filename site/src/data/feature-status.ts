export type FeatureStatus = {
  title: string;
  state: "Available" | "Experimental" | "Reserved";
  gate?: string;
  validation: string;
  details: string;
};

export const featureStatus: FeatureStatus[] = [
  {
    title: "Core Linux, /sim, HTTP, MQTT, and serial",
    state: "Experimental",
    validation:
      "Guest and automated integration coverage is green; the broad live KSA pass remains open.",
    details:
      "The transport plumbing has been exercised with a real guest. Flight behavior still needs its in-game checklist.",
  },
  {
    title: "MCP",
    state: "Experimental",
    gate: "mcp_enabled = true",
    validation:
      "Schema and static coverage are complete; the live mod lifecycle pass remains open.",
    details: "MCP is a loopback-only logical JSON API for agents, not a filesystem mirror.",
  },
  {
    title: "Screen stream",
    state: "Experimental",
    gate: "display_enabled = true",
    validation:
      "An informal in-game pass succeeded; the formal stream and soak checklist remains open.",
    details:
      "The Kitty image stream is intentionally a terminal feature and is excluded from MCP v1.",
  },
  {
    title: "Camera, audio, schedules, and FX",
    state: "Experimental",
    gate: "camera_enabled, audio_enabled, schedule_enabled, or debug_namespace",
    validation:
      "Code and game-free tests are complete; their dedicated live-flight checklists remain open.",
    details:
      "These are shipped surfaces with real gates and limits. Treat a first flight as a friendly test mission.",
  },
  {
    title: "Welds, IVA physics, scale, force render, and thug life",
    state: "Experimental",
    gate: "debug_namespace = true, plus feature-specific gates where noted",
    validation: "Code is complete; KSA render and physics behavior still needs a live-flight pass.",
    details: "IVA physics starts disabled and does no work until you enable it.",
  },
  {
    title: "CCSDS and MIL-STD-1553 buses",
    state: "Reserved",
    gate: "bus_ccsds / bus_1553",
    validation: "Configuration placeholders exist, but neither bus is served yet.",
    details: "The virtio serial bridge is the supported hardware-style channel today.",
  },
];
