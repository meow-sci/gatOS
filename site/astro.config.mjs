// @ts-check
import { defineConfig } from "astro/config";
import starlight from "@astrojs/starlight";
import remarkMath from "remark-math";
import rehypeKatex from "rehype-katex";
import { KATEX_ERROR_COLOR, rehypeKatexStrict } from "./rehype-katex-strict.mjs";
import { unified } from "@astrojs/markdown-remark";

// https://astro.build/config
export default defineConfig({
  // Published on GitHub Pages under the meow-sci org's custom domain, served at
  // https://meow.science.fail/gatOS/ (sibling project flexo lives at /flexo/).
  // `site` + `base` make Starlight emit correct absolute/prefixed URLs; the base
  // must match the repo name exactly (case-sensitive path segment).
  site: "https://meow.science.fail",
  base: "/gatOS/",
  integrations: [
    starlight({
      editLink: {
        baseUrl: "https://github.com/meow-sci/gatOS/edit/main/",
      },
      title: "gatOS",
      customCss: ["./src/styles/custom.css"],
      social: [{ icon: "github", label: "GitHub", href: "https://github.com/meow-sci/gatOS" }],
      sidebar: [
        {
          label: "Intro",
          items: [{ autogenerate: { directory: "intro" } }],
        },
        {
          label: "Guides",
          items: [
            {
              label: "Foundations",
              items: [
                { label: "Read telemetry", link: "/guides/read-telemetry/" },
                { label: "Throttle and ignite", link: "/guides/throttle-and-ignite/" },
                { label: "Stage and use modules", link: "/guides/staging-and-modules/" },
                { label: "Attitude modes", link: "/guides/attitude-modes/" },
                { label: "Wait in simulation time", link: "/guides/wait-in-sim-time/" },
                { label: "Reference frames", link: "/guides/reference-frames/" },
                { label: "Use gatos-io", link: "/guides/gatos-io/" },
                {
                  label: "Point at a parent body",
                  link: "/guides/vessel-control-point-at-parent/",
                },
                { label: "Hold a lock", link: "/guides/hold-a-lock/" },
                { label: "Orbital math", link: "/guides/orbital-math/" },
                { label: "Schedule a burn", link: "/guides/schedule-a-burn/" },
                { label: "React to events", link: "/guides/react-to-events/" },
                { label: "Atomic batches", link: "/guides/atomic-batches/" },
                { label: "Timed sequences", link: "/guides/timed-sequences/" },
                { label: "RCS control", link: "/guides/rcs-control/" },
                { label: "Docking and parts", link: "/guides/docking-and-parts/" },
                { label: "Closed-loop guidance", link: "/guides/closed-loop-guidance/" },
              ],
            },
            {
              label: "Flight projects",
              collapsed: true,
              items: [
                { label: "Teleport into orbit", link: "/guides/teleport-into-orbit/" },
                {
                  label: "Track a vessel with a light",
                  link: "/guides/searchlight-track-a-vessel/",
                },
                { label: "EVA taxi to a part", link: "/guides/eva-taxi-to-a-part/" },
                { label: "Punch it", link: "/guides/punch-it/" },
              ],
            },
            {
              label: "Interfaces and guest Linux",
              collapsed: true,
              items: [
                { label: "Build an HTTP dashboard", link: "/guides/http-dashboard/" },
                { label: "Stream telemetry with MQTT", link: "/guides/mqtt-telemetry/" },
                { label: "Connect over serial", link: "/guides/serial-link/" },
                { label: "Mount a host folder", link: "/guides/host-mounts/" },
              ],
            },
            {
              label: "Creative studio",
              collapsed: true,
              items: [
                { label: "Direct a camera shot", link: "/guides/direct-a-camera-shot/" },
                { label: "Edit a camera shot", link: "/guides/edit-camera-shot/" },
                {
                  label: "Watch the game in a terminal",
                  link: "/guides/watch-the-game-in-your-terminal/",
                },
                { label: "Build a soundboard", link: "/guides/build-a-soundboard/" },
                { label: "Throw a light show", link: "/guides/throw-a-light-show/" },
                { label: "Edit engine plumes", link: "/guides/edit-engine-plumes/" },
                { label: "Open the debug sandbox", link: "/guides/debug-sandbox/" },
                { label: "Weld vessels", link: "/guides/weld-vessels/" },
                {
                  label: "Float objects in the cabin",
                  link: "/guides/float-objects-in-the-cabin/",
                },
                { label: "Use visual cheats", link: "/guides/visual-cheats/" },
              ],
            },
            {
              label: "Showcase programs",
              collapsed: true,
              items: [
                { label: "Example programs", link: "/guides/examples/" },
                { label: "Land-o-matic", link: "/guides/land-o-matic/" },
                { label: "SkyCaptain", link: "/guides/skycaptain/" },
                { label: "Apollo Guidance Computer", link: "/guides/apollo-guidance-computer/" },
              ],
            },
          ],
        },
        {
          label: "MCP",
          items: [
            { label: "Overview", link: "/mcp/" },
            { label: "Connect and get started", link: "/mcp/getting-started/" },
            { label: "Agent playbooks", link: "/mcp/playbooks/" },
            { label: "Conventions and errors", link: "/mcp/conventions/" },
            { label: "Tool directory", link: "/mcp/tools/" },
            { label: "Resource directory", link: "/mcp/resources/" },
          ],
        },
        {
          label: "Reference",
          items: [
            { label: "Reference overview", link: "/reference/" },
            {
              label: "Core model",
              items: [
                { label: "Conventions", link: "/reference/conventions/" },
                { label: "Frames and units", link: "/reference/frames-and-units/" },
                { label: "Time, system, and bodies", link: "/reference/time-system-bodies/" },
                { label: "Vessels", link: "/reference/vessels/" },
                { label: "Vessel modules", link: "/reference/vessel-modules/" },
                { label: "Controls", link: "/reference/controls/" },
                { label: "Attitude modes", link: "/reference/attitude-modes/" },
              ],
            },
            {
              label: "Automation and media",
              collapsed: true,
              items: [
                { label: "Batches and schedules", link: "/reference/batches-and-schedules/" },
                { label: "Camera", link: "/reference/camera/" },
                { label: "Audio and display", link: "/reference/audio-and-display/" },
                { label: "Debug and visual tools", link: "/reference/debug-and-visual-tools/" },
              ],
            },
            {
              label: "Transports and configuration",
              collapsed: true,
              items: [
                { label: "HTTP", link: "/reference/http/" },
                { label: "MQTT and serial", link: "/reference/mqtt-serial/" },
                { label: "Configuration and mounts", link: "/reference/configuration-and-mounts/" },
              ],
            },
            {
              label: "Status and limitations",
              collapsed: true,
              items: [
                { label: "Events and status", link: "/reference/events-status/" },
                { label: "Known limitations", link: "/reference/status-and-limitations/" },
              ],
            },
          ],
        },
      ],
    }),
  ],
  // this will setup Markdown/MDX LaTeX -> MathML at build time
  markdown: {
    processor: unified({
      remarkPlugins: [remarkMath],
      rehypePlugins: [
        [rehypeKatex, { output: "mathml", errorColor: KATEX_ERROR_COLOR }],
        rehypeKatexStrict,
      ],
    }),
  },
});
